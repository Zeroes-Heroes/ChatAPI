using Database.Context;
using Services.Sessions.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Interface;

/// <summary>
/// Per-device opaque refresh tokens, hashed at rest, rotated transactionally with idempotent-retry
/// and reuse/family-revocation detection - docs/sync-contract-v1.md's "Sessions, enrollment and key
/// protection" section. Caller-owns-the-transaction for session creation (so it commits atomically
/// with the device-register that authorizes it); <see cref="RotateAsync"/> manages its own complete
/// transaction (refresh is a self-contained operation with no other caller-side mutation to join).
/// </summary>
public interface IRefreshTokenService
{
	/// <summary>Creates a brand new session with a fresh refresh token. Does not call SaveChanges - the caller (device-register) commits this together with the registration/challenge-consumption it belongs to.</summary>
	Task<NewSession> CreateSessionAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string localInstanceId, long trustVersion,
		DateTime now, CancellationToken ct = default);

	/// <summary>
	/// Rotates the refresh token for an already device-proof-verified request (the caller must have
	/// already consumed the matching SessionBinding challenge and verified its proof - this method
	/// re-validates current device/account trust independently, at commit, under a row lock, but does
	/// not itself verify any signature). Manages its own transaction; never throws for an ordinary
	/// rejection - every outcome docs/sync-contract-v1.md's RefreshOutcome lists is a normal return
	/// value, not an exception.
	/// </summary>
	Task<WireContracts.RefreshSessionResult> RotateAsync(
		AppDbContext db, string accountId, string sessionId, string refreshOperationId, string suppliedRawRefreshToken,
		CancellationToken ct = default);
}
