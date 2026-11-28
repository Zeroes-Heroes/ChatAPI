using System.Globalization;
using System.Security.Claims;
using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Crypto;
using Services.Keys;
using Services.Keys.Interface;
using Services.Keys.Models;
using Services.Recovery.Interface;
using Services.Recovery.Models;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Recovery.Service;

/// <summary>
/// See <see cref="IRecoveryEnvelopeService"/>. Thin over two existing seams: the stage-04 key module for
/// at-rest protection (purpose <see cref="KeyProtectionPurposes.RecoveryEnvelope"/>) and the stage-05
/// trust checker for commit-time authority.
/// </summary>
public sealed class RecoveryEnvelopeService(IMessageKeyProtector protector, ISessionTrustChecker trustChecker) : IRecoveryEnvelopeService
{
	/// <summary>Canonical layout inside <see cref="SyncRecoveryEnvelopeEntity.ProtectedEnvelope"/>: nonce(12) || ciphertext || tag(16).</summary>
	private const int ClientNonceLength = 12;
	private const int ClientTagLength = 16;

	/// <summary>
	/// Hard cap on stored versions per account context. NOTHING is ever deleted - any uploaded version
	/// may be the secret the password manager actually holds (a save can succeed and the confirmation
	/// still be lost) - so the cap is what keeps that set bounded instead of pruning.
	/// </summary>
	private const int MaxStoredVersions = 5;

	/// <summary>Bounds one escrow upload; the client pages larger sets.</summary>
	private const int MaxEscrowEntriesPerRequest = 100;

	public async Task<WireContracts.RecoveryEnvelopeView> GetCurrentAsync(AppDbContext db, string accountId, CancellationToken ct = default)
	{
		IReadOnlyList<RecoveryCandidate> candidates = await GetRecoveryCandidatesAsync(db, accountId, ct);
		long? confirmed = await ConfirmedVersionAsync(db, accountId, ct);
		long newest = await db.SyncRecoveryEnvelopes.AsNoTracking()
			.Where(e => e.AccountId == accountId)
			.MaxAsync(e => (long?)e.RecoveryVersion, ct) ?? 0;

		// Each candidate carries its own PUBLIC verification key (see RecoveryPackage): a device seals new
		// author keys to EVERY accepted version, because any of them may be the secret the password
		// manager actually holds - exactly the set this method already offers for recovery.
		return new WireContracts.RecoveryEnvelopeView(
			accountId,
			confirmed?.ToString(CultureInfo.InvariantCulture),
			(newest + 1).ToString(CultureInfo.InvariantCulture),
			candidates.Select(c => c.Package).ToList());
	}

	public async Task<IReadOnlyList<RecoveryCandidate>> GetRecoveryCandidatesAsync(AppDbContext db, string accountId, CancellationToken ct = default)
	{
		// EVERY stored version, newest first. Which one the user's password manager holds is not
		// knowable here: a save can succeed while the confirmation is lost, and two devices' dialogs can
		// resolve out of order - so no version is ever excluded, and the set is bounded by refusing
		// further uploads instead.
		List<SyncRecoveryEnvelopeEntity> rows = await db.SyncRecoveryEnvelopes.AsNoTracking()
			.Where(e => e.AccountId == accountId)
			.OrderByDescending(e => e.RecoveryVersion)
			.ToListAsync(ct);

		return rows.ConvertAll(row => new RecoveryCandidate(row.RecoveryVersion, ToPackage(accountId, row), row.VerificationSpki));
	}

	public async Task<WireContracts.RecoveryEnvelopeView> StoreAsync(
		AppDbContext db, ClaimsPrincipal caller, WireContracts.StoreRecoveryEnvelopeRequest request,
		DateTime now, CancellationToken ct = default)
	{
		string accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId)
			?? throw new SessionNotTrustedException("Caller has no account context claim.");
		string deviceRegistrationId = caller.FindFirstValue(SessionClaimTypes.DeviceRegistrationId)
			?? throw new SessionNotTrustedException("Caller has no device claim.");

		if (request.ProtocolVersion != WireContracts.SyncProtocol.Version)
		{
			throw new RecoveryEnvelopeInvalidException("Unsupported protocol version.");
		}
		if (!WireContracts.SyncProtocol.IsSequence(request.Version)
			|| !long.TryParse(request.Version, NumberStyles.None, CultureInfo.InvariantCulture, out long requestedVersion)
			|| requestedVersion < 1)
		{
			throw new RecoveryEnvelopeInvalidException("Recovery version is not a canonical positive decimal.");
		}

		// Bounds first: the metadata is what a recovering device feeds its own bounded derivation.
		RecoveryKdfMetadata kdf = RecoveryKdfMetadata.FromWire(request.Kdf);

		if (!Base64Url.TryDecode(request.Content.Nonce, out byte[] clientNonce) || clientNonce.Length != ClientNonceLength)
		{
			throw new RecoveryEnvelopeInvalidException($"Envelope nonce must be canonical base64url of exactly {ClientNonceLength} bytes.");
		}
		if (!Base64Url.TryDecode(request.Content.Tag, out byte[] clientTag) || clientTag.Length != ClientTagLength)
		{
			throw new RecoveryEnvelopeInvalidException($"Envelope tag must be canonical base64url of exactly {ClientTagLength} bytes.");
		}
		if (!Base64Url.TryDecode(request.Content.Ciphertext, out byte[] clientCiphertext) || clientCiphertext.Length == 0)
		{
			throw new RecoveryEnvelopeInvalidException("Envelope ciphertext must be non-empty canonical base64url.");
		}
		if (!Base64Url.TryDecode(request.VerificationKeySpki, out byte[] verificationSpki) || !IsP256Spki(verificationSpki))
		{
			throw new RecoveryEnvelopeInvalidException("Verification key must be canonical base64url DER SubjectPublicKeyInfo for P-256.");
		}

		// Canonical round-trip proof: what is persisted must re-emit as the exact AAD bytes the sealing
		// device authenticated.
		_ = RecoveryAad.Build(accountId, requestedVersion, kdf);

		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
		await LockAndRequireTrustedAsync(db, caller, accountId, tx, ct);

		// plan/06 asks for ONE recovery password per account context, not a rotation protocol: once a
		// version is confirmed, a second setup could only put a second secret in the same password-
		// manager entry and make one of them unrecoverable. A fresh start (new context) is the way to
		// start over.
		if (await ConfirmedVersionAsync(db, accountId, ct) is not null)
		{
			throw new RecoveryAlreadySetUpException("This account context already has a confirmed recovery password.");
		}

		int stored = await db.SyncRecoveryEnvelopes.CountAsync(e => e.AccountId == accountId, ct);
		if (stored >= MaxStoredVersions)
		{
			// Every earlier attempt is still accepted for recovery, so refusing here is the only safe
			// way to stay bounded: finish one of them instead of adding another unresolved secret.
			throw new RecoveryEnvelopeInvalidException("Too many unconfirmed recovery setups for this account context.");
		}

		long nextVersion = (await db.SyncRecoveryEnvelopes.AsNoTracking()
			.Where(e => e.AccountId == accountId)
			.MaxAsync(e => (long?)e.RecoveryVersion, ct) ?? 0) + 1;
		if (requestedVersion != nextVersion)
		{
			// The version is inside the client's AAD, so the server cannot renumber it on the client's
			// behalf - a stale version must be re-sealed by the device.
			throw new RecoveryEnvelopeInvalidException("Recovery version is not the version currently on offer.");
		}

		byte[] clientEnvelope = [.. clientNonce, .. clientCiphertext, .. clientTag];
		ProtectedKeyMaterial wrapped = protector.Protect(clientEnvelope, ContextFor(accountId, nextVersion));

		db.SyncRecoveryEnvelopes.Add(new SyncRecoveryEnvelopeEntity(
			accountId, nextVersion, KeyProtectionFormatVersions.V1, kdf.Algorithm, kdf.Version,
			kdf.Salt, kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism,
			wrapped.MasterKeyVersion, wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag,
			verificationSpki, deviceRegistrationId, now));
		await db.SaveChangesAsync(ct);

		await tx.CommitAsync(ct);
		return await GetCurrentAsync(db, accountId, ct);
	}

	public async Task<WireContracts.RecoveryEnvelopeView> ConfirmAsync(
		AppDbContext db, ClaimsPrincipal caller, string version, DateTime now, CancellationToken ct = default)
	{
		string accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId)
			?? throw new SessionNotTrustedException("Caller has no account context claim.");
		if (!WireContracts.SyncProtocol.IsSequence(version)
			|| !long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out long confirmedVersion))
		{
			throw new RecoveryEnvelopeInvalidException("Recovery version is not a canonical decimal.");
		}

		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
		await LockAndRequireTrustedAsync(db, caller, accountId, tx, ct);

		if (!await db.SyncRecoveryEnvelopes.AsNoTracking()
			.AnyAsync(e => e.AccountId == accountId && e.RecoveryVersion == confirmedVersion, ct))
		{
			throw new RecoveryEnvelopeInvalidException("No such recovery version for this account context.");
		}

		long? alreadyConfirmed = await ConfirmedVersionAsync(db, accountId, ct);
		if (alreadyConfirmed is not null && alreadyConfirmed != confirmedVersion)
		{
			// Another device's dialog resolved first. Nothing is lost: this version stays stored and is
			// still accepted for recovery, so the secret this caller just saved still works.
			throw new RecoveryAlreadySetUpException("Another recovery password has already been confirmed for this account context.");
		}

		// Idempotent; no row is ever removed, because any stored version may be the one the user's
		// password manager actually holds.
		await db.Database.ExecuteSqlInterpolatedAsync(
			$"""
			UPDATE "SyncRecoveryEnvelopes" SET "ConfirmedAtUtc" = {now}
			WHERE "AccountId" = {accountId} AND "RecoveryVersion" = {confirmedVersion} AND "ConfirmedAtUtc" IS NULL
			""", ct);

		await tx.CommitAsync(ct);
		return await GetCurrentAsync(db, accountId, ct);
	}

	public async Task<IReadOnlyList<WireContracts.AuthorKeyEscrowEntry>> GetAuthorKeyEscrowsAsync(
		AppDbContext db, string accountId, CancellationToken ct = default)
	{
		List<SyncAuthorKeyEscrowEntity> rows = await db.SyncAuthorKeyEscrows.AsNoTracking()
			.Where(e => e.AccountId == accountId)
			.OrderByDescending(e => e.RecoveryVersion)
			.ToListAsync(ct);

		return rows.ConvertAll(row => ToEntry(accountId, row));
	}

	public async Task<int> StoreAuthorKeyEscrowsAsync(
		AppDbContext db, ClaimsPrincipal caller, WireContracts.StoreAuthorKeyEscrowRequest request,
		DateTime now, CancellationToken ct = default)
	{
		string accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId)
			?? throw new SessionNotTrustedException("Caller has no account context claim.");
		string deviceRegistrationId = caller.FindFirstValue(SessionClaimTypes.DeviceRegistrationId)
			?? throw new SessionNotTrustedException("Caller has no device claim.");

		if (request.ProtocolVersion != WireContracts.SyncProtocol.Version)
		{
			throw new RecoveryEnvelopeInvalidException("Unsupported protocol version.");
		}
		if (request.Entries.Count == 0 || request.Entries.Count > MaxEscrowEntriesPerRequest)
		{
			throw new RecoveryEnvelopeInvalidException($"An escrow request carries between 1 and {MaxEscrowEntriesPerRequest} entries.");
		}

		// Everything is validated before the transaction opens; nothing here needs a lock to be decided.
		List<(WireContracts.AuthorKeyEscrowEntry Entry, long Version, byte[] Ephemeral, byte[] Sealed)> parsed = [.. request.Entries.Select(ParseEscrowEntry)];

		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
		await LockAndRequireTrustedAsync(db, caller, accountId, tx, ct);

		HashSet<long> storedVersions = [.. await db.SyncRecoveryEnvelopes.AsNoTracking()
			.Where(e => e.AccountId == accountId)
			.Select(e => e.RecoveryVersion)
			.ToListAsync(ct)];
		HashSet<string> alreadyEscrowed = [.. await db.SyncAuthorKeyEscrows.AsNoTracking()
			.Where(e => e.AccountId == accountId)
			.Select(e => e.RecoveryVersion + ":" + e.AuthorKeyId)
			.ToListAsync(ct)];

		int added = 0;
		foreach ((WireContracts.AuthorKeyEscrowEntry entry, long version, byte[] ephemeral, byte[] sealedPayload) in parsed)
		{
			if (!storedVersions.Contains(version))
			{
				throw new RecoveryEnvelopeInvalidException("Escrow entry names a recovery version this account context does not have.");
			}
			// Idempotent: a retry after a lost response must not rewrite an entry a recovering device
			// may already be able to open.
			if (!alreadyEscrowed.Add(version + ":" + entry.AuthorKeyId))
			{
				continue;
			}

			ProtectedKeyMaterial wrapped = protector.Protect(sealedPayload, EscrowContextFor(accountId, version, entry.AuthorKeyId));
			db.SyncAuthorKeyEscrows.Add(new SyncAuthorKeyEscrowEntity(
				accountId, version, entry.AuthorKeyId, entry.ChatId, entry.AuthorKeyVersion,
				KeyProtectionFormatVersions.V1, ephemeral, wrapped.MasterKeyVersion, wrapped.Nonce,
				wrapped.Ciphertext, wrapped.Tag, deviceRegistrationId, now));
			added++;
		}

		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		return added;
	}

	private static (WireContracts.AuthorKeyEscrowEntry, long, byte[], byte[]) ParseEscrowEntry(WireContracts.AuthorKeyEscrowEntry entry)
	{
		if (!WireContracts.SyncProtocol.IsSequence(entry.RecoveryVersion)
			|| !long.TryParse(entry.RecoveryVersion, NumberStyles.None, CultureInfo.InvariantCulture, out long version)
			|| version < 1)
		{
			throw new RecoveryEnvelopeInvalidException("Escrow entry version is not a canonical positive decimal.");
		}
		if (!RestrictedAsciiId.IsValid(entry.AuthorKeyId) || !RestrictedAsciiId.IsValid(entry.ChatId))
		{
			throw new RecoveryEnvelopeInvalidException("Escrow entry author key id / chat id is not a restricted ASCII id.");
		}
		if (!WireContracts.SyncProtocol.IsSequence(entry.AuthorKeyVersion))
		{
			throw new RecoveryEnvelopeInvalidException("Escrow entry author key version is not a canonical decimal.");
		}
		if (!Base64Url.TryDecode(entry.EphemeralPublicKeySpki, out byte[] ephemeral) || !IsP256Spki(ephemeral))
		{
			throw new RecoveryEnvelopeInvalidException("Escrow entry ephemeral key must be canonical base64url DER SubjectPublicKeyInfo for P-256.");
		}
		if (!Base64Url.TryDecode(entry.Content.Nonce, out byte[] nonce) || nonce.Length != ClientNonceLength
			|| !Base64Url.TryDecode(entry.Content.Tag, out byte[] tag) || tag.Length != ClientTagLength
			|| !Base64Url.TryDecode(entry.Content.Ciphertext, out byte[] ciphertext) || ciphertext.Length == 0)
		{
			throw new RecoveryEnvelopeInvalidException("Escrow entry content is not canonically framed (12-byte nonce, non-empty ciphertext, 16-byte tag).");
		}

		return (entry, version, ephemeral, [.. nonce, .. ciphertext, .. tag]);
	}

	private WireContracts.AuthorKeyEscrowEntry ToEntry(string accountId, SyncAuthorKeyEscrowEntity row)
	{
		byte[] sealedPayload = protector.Unprotect(
			new ProtectedKeyMaterial(row.MasterKeyVersion, row.Nonce, row.ProtectedPayload, row.Tag),
			EscrowContextFor(accountId, row.RecoveryVersion, row.AuthorKeyId));
		if (sealedPayload.Length <= ClientNonceLength + ClientTagLength)
		{
			throw new RecoveryEnvelopeInvalidException("Stored escrow entry is shorter than its own framing.");
		}

		return new WireContracts.AuthorKeyEscrowEntry(
			row.RecoveryVersion.ToString(CultureInfo.InvariantCulture),
			row.AuthorKeyId,
			row.ChatId,
			row.AuthorKeyVersion,
			Base64Url.Encode(row.EphemeralSpki),
			new WireContracts.EncryptedContent(
				Base64Url.Encode(sealedPayload.AsSpan(0, ClientNonceLength).ToArray()),
				Base64Url.Encode(sealedPayload.AsSpan(ClientNonceLength, sealedPayload.Length - ClientNonceLength - ClientTagLength).ToArray()),
				Base64Url.Encode(sealedPayload.AsSpan(sealedPayload.Length - ClientTagLength, ClientTagLength).ToArray())));
	}

	/// <summary>Key id is "version:authorKeyId", so an entry can only be unwrapped as the one it claims to be.</summary>
	private static KeyProtectionContext EscrowContextFor(string accountId, long recoveryVersion, string authorKeyId) =>
		new(KeyProtectionFormatVersions.V1, KeyProtectionPurposes.AuthorKeyEscrow, accountId,
			$"{recoveryVersion.ToString(CultureInfo.InvariantCulture)}:{authorKeyId}");

	/// <summary>account -> device -> session locks, then the SHARED trust rule under them: authority granted at HTTP entry is not authority at commit.</summary>
	private async Task LockAndRequireTrustedAsync(AppDbContext db, ClaimsPrincipal caller, string accountId, IDbContextTransaction tx, CancellationToken ct)
	{
		await db.SyncAccounts
			.FromSqlInterpolated($"""SELECT * FROM "SyncAccounts" WHERE "Id" = {accountId} FOR UPDATE""")
			.AsNoTracking()
			.SingleAsync(ct);

		TrustCheckResult trust = await trustChecker.CheckAndLockDeviceForMutationAsync(caller, ct);
		if (!trust.IsTrusted)
		{
			await tx.RollbackAsync(ct);
			throw new SessionNotTrustedException(trust.FailureReason ?? "Caller is no longer trusted.");
		}
	}

	private static async Task<long?> ConfirmedVersionAsync(AppDbContext db, string accountId, CancellationToken ct) =>
		await db.SyncRecoveryEnvelopes.AsNoTracking()
			.Where(e => e.AccountId == accountId && e.ConfirmedAtUtc != null)
			.MaxAsync(e => (long?)e.RecoveryVersion, ct);

	private WireContracts.RecoveryPackage ToPackage(string accountId, SyncRecoveryEnvelopeEntity row)
	{
		RecoveryKdfMetadata kdf = new(row.KdfAlgorithm, row.KdfVersion, row.Salt, row.MemoryKiB, row.Iterations, row.Parallelism);
		byte[] clientEnvelope = protector.Unprotect(
			new ProtectedKeyMaterial(row.MasterKeyVersion, row.Nonce, row.ProtectedEnvelope, row.Tag),
			ContextFor(accountId, row.RecoveryVersion));
		if (clientEnvelope.Length <= ClientNonceLength + ClientTagLength)
		{
			throw new RecoveryEnvelopeInvalidException("Stored envelope is shorter than its own framing.");
		}

		return new WireContracts.RecoveryPackage(
			WireContracts.SyncProtocol.Version,
			accountId,
			row.RecoveryVersion.ToString(CultureInfo.InvariantCulture),
			kdf.ToWire(),
			new WireContracts.EncryptedContent(
				Base64Url.Encode(clientEnvelope.AsSpan(0, ClientNonceLength).ToArray()),
				Base64Url.Encode(clientEnvelope.AsSpan(ClientNonceLength, clientEnvelope.Length - ClientNonceLength - ClientTagLength).ToArray()),
				Base64Url.Encode(clientEnvelope.AsSpan(clientEnvelope.Length - ClientTagLength, ClientTagLength).ToArray())),
			Base64Url.Encode(row.VerificationSpki));
	}

	/// <summary>Owner context is the account context; key id is the recovery version, so a row can only be unwrapped as the version it claims to be.</summary>
	private static KeyProtectionContext ContextFor(string accountId, long recoveryVersion) =>
		new(KeyProtectionFormatVersions.V1, KeyProtectionPurposes.RecoveryEnvelope, accountId,
			recoveryVersion.ToString(CultureInfo.InvariantCulture));

	/// <summary>Rejected at upload, not at recovery time: a malformed verification key would otherwise only surface when someone actually needs to recover.</summary>
	private static bool IsP256Spki(byte[] spki)
	{
		try
		{
			using System.Security.Cryptography.ECDsa ecdsa = System.Security.Cryptography.ECDsa.Create();
			ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
			return ecdsa.KeySize == 256
				&& string.Equals(ecdsa.ExportParameters(false).Curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal);
		}
		catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
		{
			return false;
		}
	}
}
