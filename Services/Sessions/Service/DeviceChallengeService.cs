using System.Security.Cryptography;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Sessions.Interface;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Service;

public sealed class DeviceChallengeService(TimeProvider timeProvider) : IDeviceChallengeService
{
	private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
	private const int NonceLength = 32;
	private const int ChallengeIdLength = 20;

	public async Task<WireContracts.DeviceChallenge> IssueEnrollmentChallengeAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string keyId, byte[] spki,
		string localInstanceId, ChallengePurpose purpose, CancellationToken ct = default)
	{
		SyncDeviceRegistrationEntity? registration = await db.SyncDeviceRegistrations
			.FirstOrDefaultAsync(d => d.AccountId == accountId && d.Id == deviceRegistrationId, ct);

		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		if (registration is null)
		{
			// A DeviceRegistrationId belongs to exactly one account context (the table's primary key is the
			// id alone), so one already registered elsewhere - e.g. an install reusing the registration from
			// the context a fresh start replaced - is refused here rather than failing as a key violation.
			// The client's answer is a new registration (Crypto/DeviceKeyContext.startNewDeviceRegistration),
			// never a claim on this one. When the owner is an ENDED context of this same verified identity,
			// say so exactly: that install can mint new ids and enroll here under THIS context's own
			// trust/barrier rules (AccountContextOwnership), and it learns nothing it did not authenticate as.
			string? ownerAccountId = await db.SyncDeviceRegistrations.AsNoTracking()
				.Where(d => d.Id == deviceRegistrationId)
				.Select(d => d.AccountId)
				.FirstOrDefaultAsync(ct);
			if (ownerAccountId is not null)
			{
				if (await AccountContextOwnership.IsSupersededContextOfSameIdentityAsync(db, accountId, ownerAccountId, ct))
				{
					throw new DeviceIdentitySupersededException("This device registration id belongs to a context this identity has since replaced.");
				}

				throw new SessionNotTrustedException("This device registration id already belongs to another account context.");
			}

			registration = new SyncDeviceRegistrationEntity(deviceRegistrationId, accountId, now, keyId, spki, isFirstDevice: false, approvedByDeviceRegistrationId: null);
			db.SyncDeviceRegistrations.Add(registration);
		}
		// Else: reuse the existing row's own key - see this method's doc comment. A Trusted
		// registration can still request a fresh Enrollment/DeviceApproval challenge (e.g. retrying an
		// interrupted re-approval after revocation); its key never changes here.

		return await CreateChallengeAsync(db, purpose, accountId, registration, registration.KeyId, registration.PublicKeySpki, now, sessionId: null, refreshOperationId: null, localInstanceId, ct);
	}

	public async Task<WireContracts.DeviceChallenge> IssueSessionBindingChallengeAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string sessionId, string refreshOperationId,
		CancellationToken ct = default)
	{
		SyncDeviceRegistrationEntity registration = await db.SyncDeviceRegistrations
			.FirstOrDefaultAsync(d => d.AccountId == accountId && d.Id == deviceRegistrationId, ct)
			?? throw new SessionNotTrustedException("Device registration does not exist for this account.");

		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		// A refresh binds an existing session, which already carries its own local instance - hence null
		// here, mirrored by CK_SyncDeviceChallenges_LocalInstanceMatchesPurpose.
		return await CreateChallengeAsync(db, ChallengePurpose.SessionBinding, accountId, registration, registration.KeyId, registration.PublicKeySpki, now, sessionId, refreshOperationId, localInstanceId: null, ct);
	}

	private static async Task<WireContracts.DeviceChallenge> CreateChallengeAsync(
		AppDbContext db, ChallengePurpose purpose, string accountId, SyncDeviceRegistrationEntity registration,
		string keyId, byte[] spki, DateTime now, string? sessionId, string? refreshOperationId, string? localInstanceId, CancellationToken ct)
	{
		string challengeId = Base64Url.Encode(RandomNumberGenerator.GetBytes(ChallengeIdLength));
		byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
		DateTime expiresAtUtc = now.Add(ChallengeLifetime);

		SyncDeviceChallengeEntity entity = new(
			challengeId, purpose, accountId, registration.Id, keyId, spki, nonce, expiresAtUtc, now, sessionId, refreshOperationId, localInstanceId);
		db.SyncDeviceChallenges.Add(entity);
		await db.SaveChangesAsync(ct);

		return new WireContracts.DeviceChallenge(
			challengeId,
			(WireContracts.ChallengePurpose)purpose,
			accountId,
			registration.Id,
			new WireContracts.DevicePublicKey(keyId, Base64Url.Encode(spki)),
			Base64Url.Encode(nonce),
			RfcTimestamp.Format(expiresAtUtc));
	}

	public async Task<SyncDeviceChallengeEntity> ConsumeAsync(AppDbContext db, string challengeId, CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		// Single atomic conditional UPDATE: two concurrent consumption attempts for the same
		// challenge can never both affect a row. Participates in the caller's ambient transaction
		// (Database.CurrentTransaction), if any.
		int affected = await db.Database.ExecuteSqlInterpolatedAsync(
			$"""
			UPDATE "SyncDeviceChallenges" SET "ConsumedAtUtc" = {now}
			WHERE "Id" = {challengeId} AND "ConsumedAtUtc" IS NULL AND "ExpiresAtUtc" > {now}
			""", ct);

		if (affected != 1)
		{
			throw new ChallengeInvalidException("Unknown challenge id, already consumed, or expired.");
		}

		return await db.SyncDeviceChallenges.AsNoTracking().FirstAsync(c => c.Id == challengeId, ct);
	}
}
