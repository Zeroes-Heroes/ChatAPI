using Database.Context;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Sessions;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Xunit;

namespace SessionTests;

[Collection(DatabaseCollection.Name)]
public class DeviceTrustServiceTests(DatabaseFixture fixture)
{
	private readonly DeviceTrustService _service = new();

	[Fact]
	public async Task DetermineEnrollmentRequirement_EmptyAccount_IsFirstDevice()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);

		EnrollmentRequirement requirement = await _service.DetermineEnrollmentRequirementAsync(db, account.Id, TestData.NewId("dev"));
		Assert.Equal(EnrollmentRequirementKind.FirstDevice, requirement.Kind);
	}

	[Fact]
	public async Task DetermineEnrollmentRequirement_WithATrustedDevice_RequiresApproval()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");

		// A SIBLING (never-before-seen) DeviceRegistrationId - not the one just created above - so this
		// genuinely exercises the "some other device is trusted" branch, not the AlreadyTrustedReauthentication one.
		EnrollmentRequirement requirement = await _service.DetermineEnrollmentRequirementAsync(db, account.Id, TestData.NewId("dev"));
		Assert.Equal(EnrollmentRequirementKind.ApprovalRequired, requirement.Kind);
	}

	[Fact]
	public async Task DetermineEnrollmentRequirement_ForADeviceThatIsItselfAlreadyTrusted_IsReauthenticationNotApproval()
	{
		// "Known still-trusted device needs valid credential+proof reauthentication when its refresh
		// session expires/requires login" - plan/05-device-sessions.md. A single trusted device could
		// never recover its own session if this fell into ApprovalRequired instead (nothing else could
		// ever approve it).
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");

		EnrollmentRequirement requirement = await _service.DetermineEnrollmentRequirementAsync(db, account.Id, device.Id);
		Assert.Equal(EnrollmentRequirementKind.AlreadyTrustedReauthentication, requirement.Kind);
		Assert.Equal(ChallengePurpose.Enrollment, requirement.Purpose);
	}

	[Fact]
	public async Task DetermineEnrollmentRequirement_ForARevokedDeviceReusingItsOwnId_StaysBlockedNotReauthenticated()
	{
		// "Never change device/key identity to escape revocation" cuts both ways: reusing the SAME
		// DeviceRegistrationId while Revoked must never be mistaken for AlreadyTrustedReauthentication.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		await _service.RevokeDeviceAsync(db, account.Id, device.Id, device.TrustVersion, device.Id, DateTime.UtcNow, session.Id);

		await using AppDbContext checkDb = fixture.CreateContext();
		EnrollmentRequirement requirement = await _service.DetermineEnrollmentRequirementAsync(checkDb, account.Id, device.Id);
		Assert.Equal(EnrollmentRequirementKind.BlockedNoTrustedDeviceAvailable, requirement.Kind);
	}

	[Fact]
	public async Task TryTrustPendingDevice_ForAnAlreadyTrustedDevice_ReauthenticatesWithoutMutatingOrRequiringApproval()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		long trustVersionBefore = device.TrustVersion;

		EnrollmentRequirement requirement = await _service.TryTrustPendingDeviceAsync(
			db, account.Id, device.Id, approvedByDeviceRegistrationId: null, hasValidApproval: false, DateTime.UtcNow);
		await db.SaveChangesAsync();

		Assert.Equal(EnrollmentRequirementKind.AlreadyTrustedReauthentication, requirement.Kind);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloaded = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == device.Id);
		Assert.Equal(DeviceTrustState.Trusted, reloaded.TrustState);
		Assert.True(reloaded.IsFirstDevice); // untouched - must not be reset to false by this path.
		Assert.Equal(trustVersionBefore, reloaded.TrustVersion); // untouched - reauthentication mutates nothing.
	}

	[Fact]
	public async Task TryTrustPendingDevice_FirstDevice_TrustsWithoutApproval()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var pending = await TestData.CreatePendingDeviceAsync(db, account, spki, "key-1");

		EnrollmentRequirement requirement = await _service.TryTrustPendingDeviceAsync(
			db, account.Id, pending.Id, approvedByDeviceRegistrationId: null, hasValidApproval: false, DateTime.UtcNow);
		await db.SaveChangesAsync();

		Assert.Equal(EnrollmentRequirementKind.FirstDevice, requirement.Kind);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloaded = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == pending.Id);
		Assert.Equal(DeviceTrustState.Trusted, reloaded.TrustState);
		Assert.True(reloaded.IsFirstDevice);
	}

	[Fact]
	public async Task TryTrustPendingDevice_WhenApprovalRequiredButMissing_Throws()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (trustedSpki, _) = TestData.NewDeviceKey();
		await TestData.CreateTrustedDeviceAsync(db, account, trustedSpki, "key-existing");

		var (newSpki, _) = TestData.NewDeviceKey();
		var pending = await TestData.CreatePendingDeviceAsync(db, account, newSpki, "key-new");

		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			_service.TryTrustPendingDeviceAsync(db, account.Id, pending.Id, approvedByDeviceRegistrationId: null, hasValidApproval: false, DateTime.UtcNow));
	}

	[Fact]
	public async Task TryTrustPendingDevice_WhenApproverNoLongerTrusted_Throws()
	{
		// "Validate trust at commit; supplied ApprovingSession is not authority" - even if the caller
		// claims a valid approval signature was produced, the approving device's CURRENT trust state
		// (revoked here, after the signature would have been produced) is what decides.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (approverSpki, _) = TestData.NewDeviceKey();
		var approver = await TestData.CreateTrustedDeviceAsync(db, account, approverSpki, "key-approver");
		var approverInstance = await TestData.CreateLocalInstanceAsync(db, approver);
		var approverSession = await TestData.CreateSessionAsync(db, account, approver, approverInstance, "raw-token-approver");

		await _service.RevokeDeviceAsync(db, account.Id, approver.Id, approver.TrustVersion, approver.Id, DateTime.UtcNow, approverSession.Id);

		var (newSpki, _) = TestData.NewDeviceKey();
		var pending = await TestData.CreatePendingDeviceAsync(db, account, newSpki, "key-new");

		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			_service.TryTrustPendingDeviceAsync(db, account.Id, pending.Id, approver.Id, hasValidApproval: true, DateTime.UtcNow));
	}

	[Fact]
	public async Task RevokeDevice_EndsSessionsBumpsTrustVersionAndSetsStickyBarrier()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		long originalTrustVersion = device.TrustVersion;
		// Self-revoke (acting device == target device) - RevokeDeviceAsync commits its own transaction
		// internally (see its own doc comment), so no further db.SaveChangesAsync() here.
		IReadOnlyList<string> ended = await _service.RevokeDeviceAsync(db, account.Id, device.Id, originalTrustVersion, device.Id, DateTime.UtcNow, session.Id);

		Assert.Contains(session.Id, ended);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedDevice = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == device.Id);
		Assert.Equal(DeviceTrustState.Revoked, reloadedDevice.TrustState);
		Assert.Equal(originalTrustVersion + 1, reloadedDevice.TrustVersion);
		Assert.NotNull(reloadedDevice.RevokedAtUtc);

		var reloadedSession = await verifyDb.SyncDeviceSessions.SingleAsync(s => s.Id == session.Id);
		Assert.NotNull(reloadedSession.EndedAtUtc);
		Assert.Null(reloadedSession.CurrentRefreshTokenHash);

		var reloadedAccount = await verifyDb.SyncAccounts.SingleAsync(a => a.Id == account.Id);
		Assert.True(reloadedAccount.RevocationBarrierActive);
	}

	[Fact]
	public async Task RevokeDevice_ThenEvenWithZeroTrustedDevices_NextRegistrationIsStillBlocked()
	{
		// "Last trusted revoked cannot turn next device into 'first'." Revoking the ONLY trusted
		// device leaves zero currently-trusted devices, which on its own (per
		// DetermineEnrollmentRequirement_EmptyAccount_IsFirstDevice above) would look like a genuine
		// first-device case - the sticky RevocationBarrierActive flag is what prevents that here.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var onlyDevice = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var onlyDeviceInstance = await TestData.CreateLocalInstanceAsync(db, onlyDevice);
		var onlyDeviceSession = await TestData.CreateSessionAsync(db, account, onlyDevice, onlyDeviceInstance, "raw-token-1");

		await _service.RevokeDeviceAsync(db, account.Id, onlyDevice.Id, onlyDevice.TrustVersion, onlyDevice.Id, DateTime.UtcNow, onlyDeviceSession.Id);

		await using AppDbContext checkDb = fixture.CreateContext();
		EnrollmentRequirement requirement = await _service.DetermineEnrollmentRequirementAsync(checkDb, account.Id, TestData.NewId("dev"));
		Assert.Equal(EnrollmentRequirementKind.BlockedNoTrustedDeviceAvailable, requirement.Kind);

		var (newSpki, _) = TestData.NewDeviceKey();
		var pending = await TestData.CreatePendingDeviceAsync(checkDb, account, newSpki, "key-new");
		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			_service.TryTrustPendingDeviceAsync(checkDb, account.Id, pending.Id, null, false, DateTime.UtcNow));
	}

	[Fact]
	public async Task RevokeDevice_AlreadyRevoked_Throws()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");
		long trustVersion = device.TrustVersion;

		await _service.RevokeDeviceAsync(db, account.Id, device.Id, trustVersion, device.Id, DateTime.UtcNow, session.Id);

		// The acting device is now itself Revoked (it just revoked itself) - acting-device revalidation
		// correctly fails this closed before it can even reach "target already revoked". The second call's
		// own actingSessionId value is irrelevant to the outcome (the acting-device check throws first) -
		// `session.Id` is reused only because a syntactically valid value is required now.
		await Assert.ThrowsAsync<SessionNotTrustedException>(() => _service.RevokeDeviceAsync(db, account.Id, device.Id, trustVersion, device.Id, DateTime.UtcNow, session.Id));
	}

	[Fact]
	public async Task RevokeDevice_WhenActingDeviceTrustVersionNoLongerMatches_ThrowsWithoutTouchingTheTarget()
	{
		// "Check acting device of revoke as well" / "accurate acting-session revalidation" - a stale
		// authorization check (the acting device's token claims a TrustVersion the device no longer has,
		// e.g. because it was itself revoked and re-trusted, or revoked, between authorization and this
		// call) must fail closed rather than silently revoking the target on the actor's stale say-so.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (actorSpki, _) = TestData.NewDeviceKey();
		var actor = await TestData.CreateTrustedDeviceAsync(db, account, actorSpki, "key-actor", isFirstDevice: true);
		var (targetSpki, _) = TestData.NewDeviceKey();
		var target = await TestData.CreateTrustedDeviceAsync(db, account, targetSpki, "key-target", isFirstDevice: false);

		long staleActingTrustVersion = actor.TrustVersion + 1; // claims an epoch the actor never actually had.

		// The acting-device TrustVersion check throws before the (now mandatory) acting-session check ever
		// runs, so this session id's own value is irrelevant to the outcome - it never needs to resolve to
		// a real row - only its presence (no more test-only null bypass) matters here.
		await Assert.ThrowsAsync<SessionNotTrustedException>(() =>
			_service.RevokeDeviceAsync(db, account.Id, actor.Id, staleActingTrustVersion, target.Id, DateTime.UtcNow, TestData.NewId("sess")));

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedTarget = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == target.Id);
		Assert.Equal(DeviceTrustState.Trusted, reloadedTarget.TrustState); // untouched.
	}

	[Fact]
	public async Task RevokeDevice_WhenTheActingSessionHasEndedIndependentlyOfTheDevice_ThrowsWithoutTouchingTheTarget()
	{
		// "Acting session of revoke must likewise still be live, not only device trusted" - simulates
		// RefreshTokenService.RotateAsync's own reuse-detection path, which ends a session WITHOUT ever
		// revoking (or touching) the device row at all. The acting device here stays genuinely Trusted;
		// only the specific session the (simulated) request's access token claims has ended.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (actorSpki, _) = TestData.NewDeviceKey();
		var actor = await TestData.CreateTrustedDeviceAsync(db, account, actorSpki, "key-actor", isFirstDevice: true);
		var instance = await TestData.CreateLocalInstanceAsync(db, actor);
		var actingSession = await TestData.CreateSessionAsync(db, account, actor, instance, "raw-token-acting");
		actingSession.EndedAtUtc = DateTime.UtcNow;
		await db.SaveChangesAsync();

		var (targetSpki, _) = TestData.NewDeviceKey();
		var target = await TestData.CreateTrustedDeviceAsync(db, account, targetSpki, "key-target", isFirstDevice: false);

		await Assert.ThrowsAsync<SessionNotTrustedException>(() =>
			_service.RevokeDeviceAsync(db, account.Id, actor.Id, actor.TrustVersion, target.Id, DateTime.UtcNow, actingSession.Id));

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedTarget = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == target.Id);
		Assert.Equal(DeviceTrustState.Trusted, reloadedTarget.TrustState); // untouched.
		var reloadedActor = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == actor.Id);
		Assert.Equal(DeviceTrustState.Trusted, reloadedActor.TrustState); // the actor DEVICE itself is untouched - only its session had ended.
	}

	[Fact]
	public async Task RevokeDevice_WhenTheActingSessionIsStillLive_SucceedsExactlyAsWithoutTheCheck()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (actorSpki, _) = TestData.NewDeviceKey();
		var actor = await TestData.CreateTrustedDeviceAsync(db, account, actorSpki, "key-actor", isFirstDevice: true);
		var instance = await TestData.CreateLocalInstanceAsync(db, actor);
		var actingSession = await TestData.CreateSessionAsync(db, account, actor, instance, "raw-token-acting");

		var (targetSpki, _) = TestData.NewDeviceKey();
		var target = await TestData.CreateTrustedDeviceAsync(db, account, targetSpki, "key-target", isFirstDevice: false);

		await _service.RevokeDeviceAsync(db, account.Id, actor.Id, actor.TrustVersion, target.Id, DateTime.UtcNow, actingSession.Id);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedTarget = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == target.Id);
		Assert.Equal(DeviceTrustState.Revoked, reloadedTarget.TrustState);
	}

	[Fact]
	public async Task TwoConcurrentFirstDeviceEnrollments_OnlyOneBecomesFirstDevice()
	{
		// Proves the account row lock in TryTrustPendingDeviceAsync plus the DB's own filtered unique
		// index (IX_SyncDeviceRegistrations_AccountId_FirstDevice) together make this a true
		// account-scoped atomic decision rather than a best-effort check-then-act.
		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (spkiA, _) = TestData.NewDeviceKey();
		var (spkiB, _) = TestData.NewDeviceKey();
		var pendingA = await TestData.CreatePendingDeviceAsync(seedDb, account, spkiA, "key-a");
		var pendingB = await TestData.CreatePendingDeviceAsync(seedDb, account, spkiB, "key-b");

		await using AppDbContext dbA = fixture.CreateContext();
		await using AppDbContext dbB = fixture.CreateContext();
		DeviceTrustService serviceA = new();
		DeviceTrustService serviceB = new();

		async Task<bool> TryEnroll(AppDbContext db, DeviceTrustService svc, string deviceId)
		{
			try
			{
				await svc.TryTrustPendingDeviceAsync(db, account.Id, deviceId, null, false, DateTime.UtcNow);
				await db.SaveChangesAsync();
				return true;
			}
			catch (DeviceApprovalRequiredException)
			{
				return false;
			}
		}

		bool[] results = await Task.WhenAll(TryEnroll(dbA, serviceA, pendingA.Id), TryEnroll(dbB, serviceB, pendingB.Id));

		// Exactly one must have become the trusted first device; the other must have hit
		// ApprovalRequired once it observed the (now) non-empty trusted-device set under the lock.
		Assert.Equal(1, results.Count(r => r));

		await using AppDbContext verifyDb = fixture.CreateContext();
		int firstDeviceCount = await verifyDb.SyncDeviceRegistrations.CountAsync(d => d.AccountId == account.Id && d.IsFirstDevice);
		Assert.Equal(1, firstDeviceCount);
	}

	[Fact]
	public async Task RevokeDevice_RacedAgainstAConcurrentEnrollmentForTheSameAccount_NeverLetsTheNewDeviceBecomeTrusted()
	{
		// Independent review finding: revoking an account's only trusted device, concurrently with a
		// new device's enrollment decision for that SAME account, must genuinely serialize (both now
		// take the account row lock, in the same order) rather than interleave as four independent
		// auto-committing statements. Whichever operation's account lock acquisition wins, the other
		// must observe its FULLY-COMMITTED result, never a transient in-between state - so the new
		// device must NEVER end up Trusted/FirstDevice no matter which order the two actually run in.
		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (ownerSpki, _) = TestData.NewDeviceKey();
		var onlyDevice = await TestData.CreateTrustedDeviceAsync(seedDb, account, ownerSpki, "key-owner", isFirstDevice: true);
		long ownerTrustVersion = onlyDevice.TrustVersion;
		var ownerInstance = await TestData.CreateLocalInstanceAsync(seedDb, onlyDevice);
		var ownerSession = await TestData.CreateSessionAsync(seedDb, account, onlyDevice, ownerInstance, "raw-token-owner");
		var (pendingSpki, _) = TestData.NewDeviceKey();
		var pending = await TestData.CreatePendingDeviceAsync(seedDb, account, pendingSpki, "key-pending");

		await using AppDbContext revokeDb = fixture.CreateContext();
		await using AppDbContext enrollDb = fixture.CreateContext();
		DeviceTrustService revokeService = new();
		DeviceTrustService enrollService = new();

		async Task RevokeTask()
		{
			await revokeService.RevokeDeviceAsync(revokeDb, account.Id, onlyDevice.Id, ownerTrustVersion, onlyDevice.Id, DateTime.UtcNow, ownerSession.Id);
		}

		async Task<bool> EnrollTask()
		{
			try
			{
				await enrollService.TryTrustPendingDeviceAsync(enrollDb, account.Id, pending.Id, null, false, DateTime.UtcNow);
				await enrollDb.SaveChangesAsync();
				return true;
			}
			catch (DeviceApprovalRequiredException)
			{
				return false;
			}
		}

		await Task.WhenAll(RevokeTask(), EnrollTask());

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedPending = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == pending.Id);
		Assert.False(reloadedPending.IsFirstDevice);
		Assert.NotEqual(DeviceTrustState.Trusted, reloadedPending.TrustState);

		var reloadedAccount = await verifyDb.SyncAccounts.SingleAsync(a => a.Id == account.Id);
		Assert.True(reloadedAccount.RevocationBarrierActive);
	}
}
