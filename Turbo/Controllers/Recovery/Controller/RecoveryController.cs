using System.Security.Claims;
using Database.Context;
using Database.Entities.Sync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services.Extensions;
using Services.Recovery;
using Services.Recovery.Interface;
using Services.Sessions;
using Services.Sessions.Interface;
using WireContracts = Services.Sync.Contracts;

namespace Turbo.Controllers.Recovery.Controller;

/// <summary>
/// Stage 06 recovery/approval endpoints (plan/06-recovery.md). Thin: resolve the caller's identity from
/// claims, call one focused service, map its outcome.
///
/// Authorization is declared PER ACTION and there is deliberately no class-level [Authorize]: controller
/// and action policies are ANDed by ASP.NET Core, and the app-session and bootstrap requirements are
/// mutually exclusive (one demands token_purpose=app, the other token_purpose=bootstrap), so a
/// class-level default policy would make every bootstrap action unreachable.
/// </summary>
[ApiController]
[Route("recovery")]
public class RecoveryController(
	AppDbContext db,
	IRecoveryEnvelopeService envelopeService,
	IRecoveryEnrollmentCoordinator recoveryEnrollment,
	IDeviceApprovalService approvalService,
	ISyncAccountProvisioningService accountProvisioning,
	TimeProvider timeProvider) : ControllerBase
{
	[Authorize]
	[HttpGet("envelope")]
	public async Task<ActionResult<WireContracts.RecoveryEnvelopeView>> GetEnvelope(CancellationToken ct) =>
		await envelopeService.GetCurrentAsync(db, AccountContextId(), ct);

	[Authorize]
	[HttpPost("envelope")]
	public async Task<ActionResult<WireContracts.RecoveryEnvelopeView>> StoreEnvelope(
		[FromBody] WireContracts.StoreRecoveryEnvelopeRequest request, CancellationToken ct)
	{
		try
		{
			// Commits its own transaction after re-checking trust under account -> device -> session
			// locks; no SaveChangesAsync belongs here.
			return await envelopeService.StoreAsync(db, User, request, timeProvider.GetUtcNow().UtcDateTime, ct);
		}
		catch (RecoveryAlreadySetUpException)
		{
			return Conflict();
		}
		catch (RecoveryEnvelopeInvalidException)
		{
			return BadRequest();
		}
		catch (SessionNotTrustedException)
		{
			return Unauthorized();
		}
	}

	[Authorize]
	[HttpPost("envelope/confirm")]
	public async Task<ActionResult<WireContracts.RecoveryEnvelopeView>> ConfirmEnvelope(
		[FromBody] WireContracts.ConfirmRecoveryEnvelopeRequest request, CancellationToken ct)
	{
		try
		{
			return await envelopeService.ConfirmAsync(db, User, request.Version, timeProvider.GetUtcNow().UtcDateTime, ct);
		}
		catch (RecoveryAlreadySetUpException)
		{
			return Conflict();
		}
		catch (RecoveryEnvelopeInvalidException)
		{
			return BadRequest();
		}
		catch (SessionNotTrustedException)
		{
			return Unauthorized();
		}
	}

	/// <summary>The author keys escrowed to this account context's recovery versions - sealed, so only a device that opened an envelope can read them.</summary>
	[Authorize]
	[HttpGet("author-keys")]
	public async Task<ActionResult<IReadOnlyList<WireContracts.AuthorKeyEscrowEntry>>> GetAuthorKeyEscrows(CancellationToken ct) =>
		Ok(await envelopeService.GetAuthorKeyEscrowsAsync(db, AccountContextId(), ct));

	/// <summary>Keeps authorship created AFTER the one-time envelope snapshot recoverable. Append-only and idempotent.</summary>
	[Authorize]
	[HttpPost("author-keys")]
	public async Task<ActionResult> StoreAuthorKeyEscrows(
		[FromBody] WireContracts.StoreAuthorKeyEscrowRequest request, CancellationToken ct)
	{
		try
		{
			await envelopeService.StoreAuthorKeyEscrowsAsync(db, User, request, timeProvider.GetUtcNow().UtcDateTime, ct);
			return NoContent();
		}
		catch (RecoveryEnvelopeInvalidException)
		{
			return BadRequest();
		}
		catch (SessionNotTrustedException)
		{
			return Unauthorized();
		}
	}

	/// <summary>Bootstrap recovery step 1: the sealed candidates plus a Recovery-purpose challenge bound to this exact device/key/local instance.</summary>
	[Authorize(Policy = "Bootstrap")]
	[HttpPost("bootstrap-challenge")]
	public async Task<ActionResult<WireContracts.RecoveryBootstrapChallenge>> RequestRecoveryChallenge(
		[FromBody] WireContracts.DeviceChallengeRequest request, CancellationToken ct)
	{
		if (!Services.Crypto.Base64Url.TryDecode(request.PublicKey.Spki, out byte[] spki)
			|| !RestrictedAsciiId.IsValid(request.DeviceRegistrationId)
			|| !RestrictedAsciiId.IsValid(request.PublicKey.KeyId)
			|| !RestrictedAsciiId.IsValid(request.LocalInstanceId))
		{
			return BadRequest();
		}

		try
		{
			return await recoveryEnrollment.RequestRecoveryChallengeAsync(
				db, User.Id(), request.DeviceRegistrationId, request.PublicKey.KeyId, spki, request.LocalInstanceId, ct);
		}
		catch (RecoveryEnvelopeInvalidException)
		{
			return NotFound();
		}
		catch (SessionNotTrustedException)
		{
			// The ids presented belong to another account context (see IDeviceChallengeService).
			return Unauthorized();
		}
		catch (DeviceApprovalRequiredException)
		{
			// The barrier refuses BEFORE any sealed candidate is released; the same answer the ordinary
			// enrollment challenge gives, so the client routes to approval instead of to the secret.
			return StatusCode(StatusCodes.Status403Forbidden, "Another currently trusted device must approve this registration.");
		}
		catch (DeviceIdentitySupersededException)
		{
			// Exact, authenticated case: the presented ids belong to a context this same identity has
			// since replaced (a fresh start performed on another install). 409 is the one answer that
			// entitles the client to its scoped transition - mint new ids and enroll into the CURRENT
			// context under that context's trust/barrier rules - which no ordinary 401/403 ever does.
			return Conflict("This device identity belongs to an account context that has been replaced.");
		}
	}

	/// <summary>Bootstrap recovery step 2: device proof + proof of recovery-secret knowledge. The barrier still outranks the secret.</summary>
	[Authorize(Policy = "Bootstrap")]
	[HttpPost("bootstrap-register")]
	public async Task<ActionResult<WireContracts.SessionTokens>> RegisterWithRecovery(
		[FromBody] WireContracts.RecoveryRegisterRequest request, CancellationToken ct)
	{
		if (!RestrictedAsciiId.IsValid(request.LocalInstanceId) || !RestrictedAsciiId.IsValid(request.Proof.ChallengeId))
		{
			return BadRequest();
		}

		try
		{
			return await recoveryEnrollment.RegisterWithRecoveryAsync(db, User.Id(), request, ct);
		}
		catch (ChallengeInvalidException)
		{
			return Unauthorized();
		}
		catch (DeviceProofInvalidException)
		{
			return Unauthorized();
		}
		catch (SessionNotTrustedException)
		{
			return Unauthorized();
		}
		catch (DeviceApprovalRequiredException)
		{
			return StatusCode(StatusCodes.Status403Forbidden, "Another currently trusted device must approve this registration.");
		}
		catch (DeviceIdentitySupersededException)
		{
			// Exact, authenticated case: the presented ids belong to a context this same identity has
			// since replaced (a fresh start performed on another install). 409 is the one answer that
			// entitles the client to its scoped transition - mint new ids and enroll into the CURRENT
			// context under that context's trust/barrier rules - which no ordinary 401/403 ever does.
			return Conflict("This device identity belongs to an account context that has been replaced.");
		}
	}

	[Authorize]
	[HttpGet("approval-requests")]
	public async Task<ActionResult<IReadOnlyList<WireContracts.PendingApprovalRequest>>> GetApprovalRequests(CancellationToken ct) =>
		Ok(await approvalService.ListPendingAsync(db, AccountContextId(), timeProvider.GetUtcNow().UtcDateTime, ct));

	[Authorize]
	[HttpPost("approvals")]
	public async Task<ActionResult<WireContracts.ApproveDeviceResult>> Approve(
		[FromBody] WireContracts.ApproveDeviceRequest request, CancellationToken ct)
	{
		if (!RestrictedAsciiId.IsValid(request.ChallengeId))
		{
			return BadRequest();
		}

		try
		{
			return await approvalService.ApproveAsync(
				db, AccountContextId(), DeviceRegistrationId(), SessionId(), request, timeProvider.GetUtcNow().UtcDateTime, ct);
		}
		catch (ApprovalRequestInvalidException)
		{
			return NotFound();
		}
		catch (DeviceProofInvalidException)
		{
			return Unauthorized();
		}
		catch (SessionNotTrustedException)
		{
			return Unauthorized();
		}
	}

	/// <summary>The author keys an approver sealed for this device; 204 when nothing is pending. Repeatable until acknowledged.</summary>
	[Authorize]
	[HttpGet("handoff")]
	public async Task<ActionResult<WireContracts.AuthorKeyHandoffView>> GetHandoff(CancellationToken ct)
	{
		WireContracts.AuthorKeyHandoffView? handoff = await approvalService.GetPendingHandoffAsync(
			db, AccountContextId(), DeviceRegistrationId(), ct);

		return handoff is null ? NoContent() : handoff;
	}

	/// <summary>Acknowledges a handoff AFTER the keys are durably imported, so a lost response never destroys the only copy.</summary>
	[Authorize]
	[HttpPost("handoff/ack")]
	public async Task<ActionResult> AcknowledgeHandoff([FromBody] WireContracts.AcknowledgeHandoffRequest request, CancellationToken ct)
	{
		if (!RestrictedAsciiId.IsValid(request.ChallengeId))
		{
			return BadRequest();
		}

		await approvalService.AcknowledgeHandoffAsync(
			db, AccountContextId(), DeviceRegistrationId(), request.ChallengeId, timeProvider.GetUtcNow().UtcDateTime, ct);
		return NoContent();
	}

	/// <summary>
	/// plan/06-recovery.md's "ново начало със същия номер": the phone is re-verified by the legacy
	/// bootstrap credential, and the result is a SEPARATE account context. The old context, its history,
	/// memberships, authorship and its own revocation barrier all stay where they are.
	///
	/// Idempotent per the caller's own durable operation id: retrying one that already succeeded returns
	/// that same successor, so a lost response never costs the user a second (destructive) restart.
	/// </summary>
	[Authorize(Policy = "Bootstrap")]
	[HttpPost("new-context")]
	public async Task<ActionResult<WireContracts.NewAccountContextResult>> StartNewContext(
		[FromBody] WireContracts.StartNewAccountContextRequest request, CancellationToken ct)
	{
		if (!RestrictedAsciiId.IsValid(request.OperationId))
		{
			return BadRequest();
		}

		int legacyUserId = User.Id();
		bool hasActiveContext = await db.SyncAccounts.AsNoTracking()
			.AnyAsync(a => a.LegacyUserId == legacyUserId && a.EndedAtUtc == null, ct);
		if (!hasActiveContext)
		{
			return NotFound();
		}

		try
		{
			SyncAccountEntity fresh = await accountProvisioning.StartNewAccountContextAsync(
				db, legacyUserId, request.OperationId, timeProvider.GetUtcNow().UtcDateTime, ct);
			// Set in the same insert that created it, for both the new and the resumed outcome.
			return new WireContracts.NewAccountContextResult(fresh.Id, fresh.PreviousAccountId!);
		}
		catch (SessionNotTrustedException)
		{
			// Either nothing to supersede any more, or this operation's own context has since been
			// superseded - in both cases the client must start a new operation, never retry this one.
			return Conflict();
		}
	}

	// AppSessionRequirement guarantees these claims on every [Authorize] action here.
	private string AccountContextId() =>
		User.FindFirstValue(SessionClaimTypes.AccountContextId)
		?? throw new InvalidOperationException("AppSessionRequirement guarantees this claim is present.");

	private string DeviceRegistrationId() =>
		User.FindFirstValue(SessionClaimTypes.DeviceRegistrationId)
		?? throw new InvalidOperationException("AppSessionRequirement guarantees this claim is present.");

	private string SessionId() =>
		User.FindFirstValue(SessionClaimTypes.SessionId)
		?? throw new InvalidOperationException("AppSessionRequirement guarantees this claim is present.");
}
