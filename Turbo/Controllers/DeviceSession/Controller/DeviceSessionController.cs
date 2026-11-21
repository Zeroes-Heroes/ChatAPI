using System.Security.Claims;
using Database.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Services.Crypto;
using Services.Extensions;
using Services.Sessions;
using Services.Sessions.Interface;
using WireContracts = Services.Sync.Contracts;

namespace Turbo.Controllers.DeviceSession.Controller;

/// <summary>
/// Stage 05 device/session endpoints (plan/05-device-sessions.md). Deliberately thin - every action
/// composes one of the focused Services/Sessions primitives/coordinators and translates its outcome
/// into an HTTP response; no business rule lives in this file. The enrollment pair uses the narrow
/// "Bootstrap" policy (legacy SMS/PIN credential only, no session yet); the refresh pair is
/// [AllowAnonymous] because a session legitimately needs to refresh after its access token has expired
/// (account/device/session are always resolved server-side from SessionId, never trusted from the
/// request - see <see cref="ISessionRefreshCoordinator"/>); revoke uses the default (app-session)
/// policy, since only an already-trusted device may revoke a sibling.
/// </summary>
[ApiController]
[Route("device-session")]
[Authorize]
public class DeviceSessionController(
	AppDbContext db,
	IDeviceEnrollmentCoordinator enrollmentCoordinator,
	ISessionRefreshCoordinator refreshCoordinator,
	IDeviceTrustService trustService,
	ISessionRevocationNotifier revocationNotifier,
	TimeProvider timeProvider,
	ILogger<DeviceSessionController> logger) : ControllerBase
{
	[Authorize(Policy = "Bootstrap")]
	[HttpPost("challenge")]
	public async Task<ActionResult<WireContracts.DeviceChallenge>> RequestChallenge([FromBody] WireContracts.DeviceChallengeRequest request, CancellationToken ct)
	{
		if (!Base64Url.TryDecode(request.PublicKey.Spki, out byte[] spki) || !RestrictedAsciiId.IsValid(request.DeviceRegistrationId) || !RestrictedAsciiId.IsValid(request.PublicKey.KeyId))
		{
			return BadRequest();
		}

		try
		{
			WireContracts.DeviceChallenge challenge = await enrollmentCoordinator.RequestEnrollmentChallengeAsync(
				db, User.Id(), request.DeviceRegistrationId, request.PublicKey.KeyId, spki, ct);
			return challenge;
		}
		catch (DeviceApprovalRequiredException)
		{
			return StatusCode(StatusCodes.Status403Forbidden, "Another currently trusted device must approve this registration.");
		}
	}

	[Authorize(Policy = "Bootstrap")]
	[HttpPost("register")]
	public async Task<ActionResult<WireContracts.SessionTokens>> Register([FromBody] WireContracts.DeviceRegisterRequest request, CancellationToken ct)
	{
		if (!RestrictedAsciiId.IsValid(request.LocalInstanceId))
		{
			return BadRequest();
		}

		try
		{
			WireContracts.SessionTokens tokens = await enrollmentCoordinator.RegisterDeviceAsync(
				db, User.Id(), request.LocalInstanceId, request.Proof, request.Approval, ct);
			return tokens;
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
	}

	[AllowAnonymous]
	[HttpPost("session-binding-challenge")]
	public async Task<ActionResult<WireContracts.DeviceChallenge>> RequestSessionBindingChallenge([FromBody] WireContracts.SessionBindingChallengeRequest request, CancellationToken ct)
	{
		if (!RestrictedAsciiId.IsValid(request.SessionId) || !RestrictedAsciiId.IsValid(request.RefreshOperationId))
		{
			return BadRequest();
		}

		try
		{
			return await refreshCoordinator.RequestSessionBindingChallengeAsync(db, request.SessionId, request.RefreshOperationId, ct);
		}
		catch (SessionNotTrustedException)
		{
			return Unauthorized();
		}
	}

	[AllowAnonymous]
	[HttpPost("refresh")]
	public async Task<ActionResult<WireContracts.RefreshSessionResult>> Refresh([FromBody] WireContracts.RefreshSessionRequest request, CancellationToken ct)
	{
		if (!RestrictedAsciiId.IsValid(request.SessionId) || !RestrictedAsciiId.IsValid(request.RefreshOperationId))
		{
			return BadRequest();
		}

		return await refreshCoordinator.RefreshAsync(db, request, ct);
	}

	[HttpPost("revoke")]
	public async Task<ActionResult> Revoke([FromBody] WireContracts.RevokeDeviceRequest request, CancellationToken ct)
	{
		if (!RestrictedAsciiId.IsValid(request.TargetDeviceRegistrationId))
		{
			return BadRequest();
		}

		string accountId = User.FindFirstValue(SessionClaimTypes.AccountContextId)
			?? throw new InvalidOperationException("AppSessionRequirement guarantees this claim is present.");
		string actingDeviceRegistrationId = User.FindFirstValue(SessionClaimTypes.DeviceRegistrationId)
			?? throw new InvalidOperationException("AppSessionRequirement guarantees this claim is present.");
		string actingTrustVersionClaim = User.FindFirstValue(SessionClaimTypes.TrustVersion)
			?? throw new InvalidOperationException("AppSessionRequirement guarantees this claim is present.");
		string actingSessionId = User.FindFirstValue(SessionClaimTypes.SessionId)
			?? throw new InvalidOperationException("AppSessionRequirement guarantees this claim is present.");

		IReadOnlyList<string> endedSessionIds;
		try
		{
			// RevokeDeviceAsync commits its own transaction internally (see its own doc comment) - no
			// further db.SaveChangesAsync() is needed or correct here; this controller has no other
			// tracked changes of its own.
			endedSessionIds = await trustService.RevokeDeviceAsync(
				db, accountId, actingDeviceRegistrationId, long.Parse(actingTrustVersionClaim),
				request.TargetDeviceRegistrationId, timeProvider.GetUtcNow().UtcDateTime, actingSessionId, ct);
		}
		catch (SessionNotTrustedException)
		{
			return NotFound();
		}

		// Enforcement for every ended session must run regardless of what happens to any OTHER session in
		// this same batch, and regardless of the inbound request's own token: the revoke above already
		// committed durably, so a client that disconnects right after (canceling `ct`) must never leave a
		// sibling session's own enforcement un-attempted. NotifyAllSessionsRevokedAsync itself aborts every
		// given session's local connections FIRST, synchronously, before attempting any of their courtesy
		// sends (run concurrently, never one session's full attempt before the next) - see its own and
		// SessionRevocationNotifier's doc comments for the exact ordering/bound guarantees. CancellationToken.None
		// here is deliberate, for the same reason.
		try
		{
			await revocationNotifier.NotifyAllSessionsRevokedAsync(endedSessionIds, WireContracts.SessionControlReason.Revoked, CancellationToken.None);
		}
		catch (Exception ex)
		{
			// Not expected to ever throw (every per-session failure is already caught/logged internally),
			// but the revoke mutation above already committed regardless - a bug in notification plumbing
			// must never be reported back to the client as if the revoke itself had failed.
			logger.LogError(ex, "Revocation notification/enforcement failed for one or more of {Count} ended session(s); the revoke itself already committed.", endedSessionIds.Count);
		}

		return NoContent();
	}
}
