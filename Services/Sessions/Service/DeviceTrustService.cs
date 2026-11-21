using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Sessions.Interface;
using Services.Sessions.Models;

namespace Services.Sessions.Service;

public sealed class DeviceTrustService : IDeviceTrustService
{
	public async Task<EnrollmentRequirement> DetermineEnrollmentRequirementAsync(AppDbContext db, string accountId, string deviceRegistrationId, CancellationToken ct = default)
	{
		bool revocationBarrierActive = await db.SyncAccounts.AsNoTracking()
			.Where(a => a.Id == accountId)
			.Select(a => a.RevocationBarrierActive)
			.SingleAsync(ct);

		DeviceTrustState? requestingDeviceTrustState = await db.SyncDeviceRegistrations.AsNoTracking()
			.Where(d => d.AccountId == accountId && d.Id == deviceRegistrationId)
			.Select(d => (DeviceTrustState?)d.TrustState)
			.FirstOrDefaultAsync(ct);

		// "Known still-trusted device needs valid credential+proof reauthentication when its refresh
		// session expires" (plan/05-device-sessions.md) - checked BEFORE the general FirstDevice/
		// ApprovalRequired/Blocked decision below, and deliberately never for a Revoked registration
		// (which must still fall through to the sticky-barrier logic - reusing the same
		// DeviceRegistrationId while revoked must never grant a free pass) or an unknown one (never
		// registered at all - a genuine new/sibling device, handled by the branches below).
		if (requestingDeviceTrustState == DeviceTrustState.Trusted)
		{
			return new EnrollmentRequirement(EnrollmentRequirementKind.AlreadyTrustedReauthentication, ChallengePurpose.Enrollment);
		}

		bool hasTrustedDevice = await db.SyncDeviceRegistrations.AsNoTracking()
			.AnyAsync(d => d.AccountId == accountId && d.TrustState == DeviceTrustState.Trusted, ct);

		if (!hasTrustedDevice && !revocationBarrierActive)
		{
			return new EnrollmentRequirement(EnrollmentRequirementKind.FirstDevice, ChallengePurpose.Enrollment);
		}
		if (hasTrustedDevice)
		{
			return new EnrollmentRequirement(EnrollmentRequirementKind.ApprovalRequired, ChallengePurpose.DeviceApproval);
		}
		// Sticky revocation barrier active and zero currently-trusted devices: "without another
		// currently trusted device old context stays blocked."
		return new EnrollmentRequirement(EnrollmentRequirementKind.BlockedNoTrustedDeviceAvailable, ChallengePurpose.DeviceApproval);
	}

	public async Task<EnrollmentRequirement> TryTrustPendingDeviceAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string? approvedByDeviceRegistrationId,
		bool hasValidApproval, DateTime now, CancellationToken ct = default)
	{
		// Row-lock the account for the duration of this decision+mutation: this is what makes two
		// concurrent enrollment attempts for the same account resolve to exactly one FirstDevice
		// winner (the DB's own filtered unique index on IsFirstDevice is the last-resort guarantee;
		// this lock is what makes the loser see the now-current state instead of racing past this
		// check with a stale read) AND what makes a concurrent RevokeDeviceAsync for the same account
		// (which takes this exact same lock, in this exact same order - see its own doc comment)
		// genuinely serialize against this decision instead of interleaving its own several mutations
		// in between this lock's acquisition and release.
		await db.SyncAccounts
			.FromSqlInterpolated($"""SELECT * FROM "SyncAccounts" WHERE "Id" = {accountId} FOR UPDATE""")
			.AsNoTracking()
			.SingleAsync(ct);

		EnrollmentRequirement requirement = await DetermineEnrollmentRequirementAsync(db, accountId, deviceRegistrationId, ct);

		if (requirement.Kind == EnrollmentRequirementKind.AlreadyTrustedReauthentication)
		{
			// Already Trusted, under the same lock, right now - nothing to mutate (IsFirstDevice/
			// ApprovedByDeviceRegistrationId must be left exactly as they already are; falling through
			// to the mutation block below would wrongly reset them based on a Kind that isn't FirstDevice/
			// ApprovalRequired).
			return requirement;
		}

		if (requirement.Kind == EnrollmentRequirementKind.BlockedNoTrustedDeviceAvailable)
		{
			throw new DeviceApprovalRequiredException("No currently trusted device is available to approve this registration.");
		}
		if (requirement.Kind == EnrollmentRequirementKind.ApprovalRequired)
		{
			if (!hasValidApproval || approvedByDeviceRegistrationId is null)
			{
				throw new DeviceApprovalRequiredException("A valid approval from a currently trusted device is required.");
			}

			// "Validate trust at commit; supplied ApprovingSession is not authority" - the approving
			// device's trust is re-checked here, under the same lock, independent of whatever was true
			// when its approval signature was produced.
			bool approverStillTrusted = await db.SyncDeviceRegistrations.AsNoTracking().AnyAsync(
				d => d.AccountId == accountId && d.Id == approvedByDeviceRegistrationId && d.TrustState == DeviceTrustState.Trusted, ct);
			if (!approverStillTrusted)
			{
				throw new DeviceApprovalRequiredException("The approving device is no longer currently trusted.");
			}
		}

		SyncDeviceRegistrationEntity device = await db.SyncDeviceRegistrations
			.SingleAsync(d => d.AccountId == accountId && d.Id == deviceRegistrationId, ct);

		device.TrustState = DeviceTrustState.Trusted;
		device.IsFirstDevice = requirement.Kind == EnrollmentRequirementKind.FirstDevice;
		device.ApprovedByDeviceRegistrationId = requirement.Kind == EnrollmentRequirementKind.ApprovalRequired ? approvedByDeviceRegistrationId : null;

		return requirement;
	}

	public async Task<IReadOnlyList<string>> RevokeDeviceAsync(
		AppDbContext db, string accountId, string actingDeviceRegistrationId, long actingTrustVersion,
		string targetDeviceRegistrationId, DateTime now, string actingSessionId, CancellationToken ct = default)
	{
		// Single transaction, committed internally (see this method's own XML doc) - the prior
		// implementation issued four independent, auto-committing statements with no lock held across
		// them, which let a concurrent enrollment decision for the SAME account observe a transient
		// intermediate state (device already revoked, account barrier not yet set) that could never be
		// observed once this method fully completes. Wrapping it in one transaction alone would not be
		// enough on its own without also taking the SAME account-row lock TryTrustPendingDeviceAsync
		// takes, in the SAME order (account before device) - that is what actually serializes the two
		// code paths against each other, not merely "every statement commits together."
		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

		await db.SyncAccounts
			.FromSqlInterpolated($"""SELECT * FROM "SyncAccounts" WHERE "Id" = {accountId} FOR UPDATE""")
			.AsNoTracking()
			.SingleAsync(ct);

		// Lock every DISTINCT device row this call touches (acting + target - the same row for a
		// self-revoke) together, in one deterministic order (sorted by Id, never "acting first" /
		// "target first"), BEFORE any session lock below - this module's tier order is strictly
		// account -> device -> session everywhere (RotateAsync, CheckAndLockDeviceForMutationAsync), and a
		// sorted order here means two concurrent revokes touching the SAME two device rows in swapped
		// roles (A revokes B while B concurrently revokes A) can never acquire them in opposite order.
		// The account lock already held above totally orders every RevokeDeviceAsync call for THIS
		// account on its own, so this cannot currently deadlock against another call to this same method
		// either way; the deterministic order is kept regardless as the one correct, future-proof shape
		// for "more than one device lock in the same transaction."
		string[] deviceIdsToLock = actingDeviceRegistrationId == targetDeviceRegistrationId
			? [actingDeviceRegistrationId]
			: new[] { actingDeviceRegistrationId, targetDeviceRegistrationId }.OrderBy(id => id, StringComparer.Ordinal).ToArray();

		Dictionary<string, SyncDeviceRegistrationEntity> lockedDevices = new();
		foreach (string deviceId in deviceIdsToLock)
		{
			SyncDeviceRegistrationEntity? lockedRow = await db.SyncDeviceRegistrations
				.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceRegistrations" WHERE "AccountId" = {accountId} AND "Id" = {deviceId} FOR UPDATE""")
				.AsNoTracking()
				.SingleOrDefaultAsync(ct);
			if (lockedRow is not null)
			{
				lockedDevices[deviceId] = lockedRow;
			}
		}

		// "Check acting device of revoke as well": the authorization pipeline already checked the acting
		// device's trust earlier in this same request, but that check and this mutation are not the same
		// instant - re-validate it here, under the lock just acquired above, against the exact
		// TrustVersion the caller's token claims, so a concurrent revocation of the ACTOR (e.g. a sibling
		// session revoking it, or it revoking itself twice concurrently) between authorization and this
		// point is never missed.
		if (!lockedDevices.TryGetValue(actingDeviceRegistrationId, out SyncDeviceRegistrationEntity? actingDevice)
			|| actingDevice.TrustState != DeviceTrustState.Trusted || actingDevice.TrustVersion != actingTrustVersion)
		{
			throw new SessionNotTrustedException("Acting device is no longer currently trusted.");
		}

		// "Acting session of revoke must likewise still be live, not only device trusted": a device can
		// stay Trusted while one of ITS OWN sessions has independently ended (RefreshTokenService.RotateAsync's
		// reuse-detection path ends a session without ever touching the device row). A real `FOR UPDATE`
		// lock, not a plain read - matched on Id/AccountId only (never EndedAtUtc: a row that has already
		// ended would otherwise match zero rows and never actually get locked at all) - so this genuinely
		// serializes against RotateAsync's own session-row lock for the exact same session: a concurrent
		// RotateAsync ending this session blocks this SELECT until it commits or rolls back, and this call
		// then re-reads the now-current (possibly just-ended) row, never a stale pre-commit one. Locked
		// AFTER both device locks above, never before, to keep the account -> device -> session tier order
		// intact everywhere in this module. Always run now - actingSessionId is a required parameter (no
		// test-only/production bypass left to opt out of this check).
		{
			SyncDeviceSessionEntity? actingSession = await db.SyncDeviceSessions
				.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceSessions" WHERE "Id" = {actingSessionId} AND "AccountId" = {accountId} FOR UPDATE""")
				.AsNoTracking()
				.SingleOrDefaultAsync(ct);
			if (actingSession is null || actingSession.DeviceRegistrationId != actingDeviceRegistrationId || actingSession.EndedAtUtc is not null)
			{
				throw new SessionNotTrustedException("Acting session is no longer currently live.");
			}
		}

		if (!lockedDevices.TryGetValue(targetDeviceRegistrationId, out SyncDeviceRegistrationEntity? device) || device.TrustState != DeviceTrustState.Trusted)
		{
			throw new SessionNotTrustedException("Target device does not exist for this account or is already revoked.");
		}

		// TrustState=2 is Revoked, =1 is Trusted (Database.Enums.Sync.DeviceTrustState). The account
		// lock above already makes this uncontended in practice, but the WHERE clause's own
		// TrustState=1 guard is kept as defense in depth against a concurrent double-revoke of the
		// same device.
		int updated = await db.Database.ExecuteSqlInterpolatedAsync(
			$"""
			UPDATE "SyncDeviceRegistrations" SET "TrustState" = 2, "TrustVersion" = "TrustVersion" + 1, "RevokedAtUtc" = {now}
			WHERE "AccountId" = {accountId} AND "Id" = {targetDeviceRegistrationId} AND "TrustState" = 1
			""", ct);
		if (updated != 1)
		{
			throw new SessionNotTrustedException("Target device was concurrently revoked.");
		}

		// Sticky, never reset - see SyncAccountEntity.RevocationBarrierActive's own doc comment. Safe to
		// set unconditionally under the account lock already held (no separate CAS needed - nothing
		// else can be mutating this same account's barrier concurrently).
		await db.Database.ExecuteSqlInterpolatedAsync(
			$"""UPDATE "SyncAccounts" SET "RevocationBarrierActive" = TRUE WHERE "Id" = {accountId} AND "RevocationBarrierActive" = FALSE""", ct);

		List<string> endedSessionIds = await db.SyncDeviceSessions.AsNoTracking()
			.Where(s => s.AccountId == accountId && s.DeviceRegistrationId == targetDeviceRegistrationId && s.EndedAtUtc == null)
			.Select(s => s.Id)
			.ToListAsync(ct);

		if (endedSessionIds.Count > 0)
		{
			await db.Database.ExecuteSqlInterpolatedAsync(
				$"""
				UPDATE "SyncDeviceSessions" SET "EndedAtUtc" = {now}, "CurrentRefreshTokenHash" = NULL, "RefreshTokenExpiresAtUtc" = NULL
				WHERE "AccountId" = {accountId} AND "DeviceRegistrationId" = {targetDeviceRegistrationId} AND "EndedAtUtc" IS NULL
				""", ct);
		}

		await tx.CommitAsync(ct);

		return endedSessionIds;
	}
}
