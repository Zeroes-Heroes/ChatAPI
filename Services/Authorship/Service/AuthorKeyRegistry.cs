using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Authorship.Interface;
using Services.Authorship.Models;
using Services.Membership;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Sync.Contracts;
using Services.Utilities;

namespace Services.Authorship.Service;

/// <summary>See <see cref="IAuthorKeyRegistry"/>.</summary>
public sealed class AuthorKeyRegistry(
	AppDbContext db, IChatAccessAuthority accessAuthority, IChatMembershipService membership,
	IAccountContextBoundary boundary, ISessionTrustChecker trustChecker, TimeProvider timeProvider)
	: IAuthorKeyRegistry
{
	public async Task<Result<AuthorKeyRegistration>> RegisterAsync(
		ClaimsPrincipal caller, AuthorKeyReference reference, byte[] spki, CancellationToken ct = default)
	{
		if (!RestrictedAsciiId.IsValid(reference.AccountContextId)
			|| !RestrictedAsciiId.IsValid(reference.ChatId)
			|| !RestrictedAsciiId.IsValid(reference.AuthorKeyId)
			|| !SyncProtocol.IsSequence(reference.AuthorKeyVersion))
		{
			return Result<AuthorKeyRegistration>.Failure("Malformed author key reference.", HttpStatusCode.BadRequest);
		}

		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		string? deviceRegistrationId = caller.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);
		if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(deviceRegistrationId))
		{
			return Result<AuthorKeyRegistration>.Failure("This session has no device context.", HttpStatusCode.BadRequest);
		}

		// The account is the SESSION's, never the payload's: a device may only ever publish its own
		// account context's authorship.
		if (!string.Equals(accountId, reference.AccountContextId, StringComparison.Ordinal))
		{
			return Result<AuthorKeyRegistration>.Failure("An author key may only be registered for the caller's own account context.", HttpStatusCode.Forbidden);
		}

		if (!int.TryParse(reference.ChatId, out int legacyChatId))
		{
			return Result<AuthorKeyRegistration>.Failure("Chat not found.", HttpStatusCode.NotFound);
		}

		// Stored only after it really is an importable P-256 public key; otherwise the failure would
		// simply move to the first message verified against it, with the registry claiming a binding it
		// cannot honour.
		if (!IsP256Spki(spki))
		{
			return Result<AuthorKeyRegistration>.Failure("Author key must be a DER SubjectPublicKeyInfo for P-256.", HttpStatusCode.BadRequest);
		}

		// Fresh start (plan/06): a superseded context's chats are not this context's at all - refused
		// identically to a chat that does not exist.
		if (await boundary.IsLegacyChatExcludedAsync(caller, legacyChatId, ct))
		{
			return Result<AuthorKeyRegistration>.Failure("Chat not found.", HttpStatusCode.NotFound);
		}

		Result<AuthorKeyRegistration>? refusal = null;
		AuthorKeyRegistration? registration = null;

		TrustCheckResult trust = await TrustedMutation.RunAsync(db, trustChecker, caller, async () =>
		{
			// The row's FKs point at SyncChats/SyncMemberships, so a chat created before stage 07 has to
			// be projected first - the same lazy projection every membership mutation performs, not a
			// second copy of it.
			try
			{
				await membership.EnsureProjectedAsync(legacyChatId, ct);
			}
			catch (ChatMembershipRefusedException)
			{
				// No such legacy chat - the same answer a chat the caller may not see gets.
				refusal = Result<AuthorKeyRegistration>.Failure("Chat not found.", HttpStatusCode.NotFound);
				return;
			}

			ChatAccess access = await accessAuthority.LockChatAndAuthorizeAsync(caller, legacyChatId, ct);
			if (!access.CanSend)
			{
				// A non-member has nothing here, and a frozen archive publishes no new authorship - it can
				// never sign anything again. Both answer the same 404 as a chat that does not exist.
				refusal = Result<AuthorKeyRegistration>.Failure("Chat not found.", HttpStatusCode.NotFound);
				return;
			}

			SyncAuthorKeyEntity? existing = await db.SyncAuthorKeys.FirstOrDefaultAsync(
				k => k.AccountId == reference.AccountContextId
					&& k.ChatId == reference.ChatId
					&& k.AuthorKeyId == reference.AuthorKeyId
					&& k.AuthorKeyVersion == reference.AuthorKeyVersion, ct);

			if (existing is not null)
			{
				if (!CryptographicOperations.FixedTimeEquals(existing.Spki, spki))
				{
					// "Once a reference is bound to SPKI, a different SPKI for that same reference is a
					// conflict" - never an overwrite, which would silently invalidate every message
					// already signed under this reference.
					refusal = Result<AuthorKeyRegistration>.Failure(
						"This author key reference is already bound to a different public key.", HttpStatusCode.Conflict);
					return;
				}

				// A lost response, a reinstall, or a recovered/approved device restoring the ORIGINAL
				// author key: the identical binding is confirmed, not written again.
				registration = new AuthorKeyRegistration(reference, AlreadyRegistered: true);
				return;
			}

			db.SyncAuthorKeys.Add(new SyncAuthorKeyEntity(
				reference.AccountContextId, reference.ChatId, reference.AuthorKeyId, reference.AuthorKeyVersion,
				spki, deviceRegistrationId, timeProvider.GetUtcNow().UtcDateTime));

			await db.SaveChangesAsync(ct);
			registration = new AuthorKeyRegistration(reference, AlreadyRegistered: false);
		}, ct);

		if (!trust.IsTrusted)
		{
			return Result<AuthorKeyRegistration>.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		return refusal ?? Result<AuthorKeyRegistration>.Success(registration!);
	}

	public async Task<byte[]?> FindSpkiAsync(AuthorKeyReference reference, CancellationToken ct = default) =>
		await db.SyncAuthorKeys.AsNoTracking()
			.Where(k => k.AccountId == reference.AccountContextId
				&& k.ChatId == reference.ChatId
				&& k.AuthorKeyId == reference.AuthorKeyId
				&& k.AuthorKeyVersion == reference.AuthorKeyVersion)
			.Select(k => k.Spki)
			.FirstOrDefaultAsync(ct);

	/// <summary>
	/// Imports the bytes as an ECDSA public key and applies exactly the curve rule
	/// <see cref="Services.Crypto.Service.MessagePackageVerifier"/> applies at verification time (OID
	/// value, not FriendlyName), so only a key the verifier can actually use is ever bound.
	/// </summary>
	private static bool IsP256Spki(byte[] spki)
	{
		const string NistP256Oid = "1.2.840.10045.3.1.7";

		try
		{
			using ECDsa ecdsa = ECDsa.Create();
			ecdsa.ImportSubjectPublicKeyInfo(spki, out int read);
			return read == spki.Length
				&& ecdsa.KeySize == 256
				&& string.Equals(ecdsa.ExportParameters(false).Curve.Oid.Value, NistP256Oid, StringComparison.Ordinal);
		}
		catch (Exception ex) when (ex is CryptographicException or ArgumentException)
		{
			return false;
		}
	}
}
