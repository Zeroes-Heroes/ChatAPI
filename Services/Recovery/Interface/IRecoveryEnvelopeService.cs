using System.Security.Claims;
using Database.Context;
using Services.Recovery.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Recovery.Interface;

/// <summary>
/// Durable storage for the account context's client-sealed recovery envelope. The server stores and
/// returns protected material, plus the PUBLIC recovery verification key; it never derives the recovery
/// key, never opens the envelope and never accepts the recovery secret in any form - a recovery password
/// is never a bearer credential here.
/// </summary>
public interface IRecoveryEnvelopeService
{
	/// <summary>Every stored version (newest first), plus the version a new upload must use.</summary>
	Task<WireContracts.RecoveryEnvelopeView> GetCurrentAsync(AppDbContext db, string accountId, CancellationToken ct = default);

	/// <summary>
	/// The accepted versions for the bootstrap recovery route: every version ever stored for this account
	/// context, newest first. The server cannot tell which attempt the system password manager actually
	/// kept, so none is ever retired and no interrupted setup can drop the secret the user holds.
	/// </summary>
	Task<IReadOnlyList<RecoveryCandidate>> GetRecoveryCandidatesAsync(AppDbContext db, string accountId, CancellationToken ct = default);

	/// <summary>
	/// Validates and stores one new envelope version. Opens its own transaction, takes account -> device
	/// -> session locks and re-runs the shared current-trust policy under them, so authority granted at
	/// HTTP entry cannot be used after a concurrent revocation/fresh start commits.
	/// </summary>
	/// <exception cref="RecoveryEnvelopeInvalidException">Malformed envelope, out-of-bounds KDF metadata, a stale version, or too many unconfirmed attempts.</exception>
	/// <exception cref="RecoveryAlreadySetUpException">This account context already has a confirmed recovery password.</exception>
	/// <exception cref="Services.Sessions.SessionNotTrustedException">The caller is no longer trusted at commit time.</exception>
	Task<WireContracts.RecoveryEnvelopeView> StoreAsync(
		AppDbContext db, ClaimsPrincipal caller, WireContracts.StoreRecoveryEnvelopeRequest request,
		DateTime now, CancellationToken ct = default);

	/// <summary>
	/// Records that the system password manager really stored this version's secret, which closes setup for
	/// this account context. No version is retired - every stored one stays a recovery candidate. Same
	/// locks/trust rule as <see cref="StoreAsync"/>; idempotent for an already-confirmed version.
	/// </summary>
	/// <exception cref="RecoveryEnvelopeInvalidException">No such version for this account context.</exception>
	/// <exception cref="RecoveryAlreadySetUpException">A different version is already confirmed.</exception>
	Task<WireContracts.RecoveryEnvelopeView> ConfirmAsync(
		AppDbContext db, ClaimsPrincipal caller, string version, DateTime now, CancellationToken ct = default);

	/// <summary>
	/// Every escrowed author key for this account context's stored versions. Sealed to the version's
	/// verification key, so only a device that opened the envelope can read them.
	/// </summary>
	Task<IReadOnlyList<WireContracts.AuthorKeyEscrowEntry>> GetAuthorKeyEscrowsAsync(
		AppDbContext db, string accountId, CancellationToken ct = default);

	/// <summary>
	/// Adds author keys to the escrow of a stored recovery version, which is what keeps authorship
	/// created AFTER the one-time envelope snapshot recoverable. Append-only and idempotent; same
	/// locks/trust rule as <see cref="StoreAsync"/>.
	/// </summary>
	/// <exception cref="RecoveryEnvelopeInvalidException">Malformed entry, or a version this account context does not have.</exception>
	Task<int> StoreAuthorKeyEscrowsAsync(
		AppDbContext db, ClaimsPrincipal caller, WireContracts.StoreAuthorKeyEscrowRequest request,
		DateTime now, CancellationToken ct = default);
}
