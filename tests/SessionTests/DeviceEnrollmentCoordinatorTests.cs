using System.Security.Cryptography;
using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Services.Crypto;
using Services.Sessions;
using Services.Sessions.Service;
using Services.Utilities.Models;
using WireContracts = Services.Sync.Contracts;
using Xunit;

namespace SessionTests;

[Collection(DatabaseCollection.Name)]
public class DeviceEnrollmentCoordinatorTests(DatabaseFixture fixture)
{
	private static byte[] DecodeBase64Url(string value) => Base64Url.TryDecode(value, out byte[] bytes) ? bytes : throw new FormatException("Invalid base64url test data");
	private static DeviceEnrollmentCoordinator MakeCoordinator(TestMasterSecretDirectory dir, FakeTimeProvider clock)
	{
		dir.WriteVersion("v1");
		SessionTokenIssuer issuer = new(Options.Create(new AppSettings { TokenIssuer = "test-issuer", TokenAudience = "test-audience", TokenSigningKey = new string('k', 64) }));
		return new DeviceEnrollmentCoordinator(
			clock,
			new SyncAccountProvisioningService(),
			new DeviceChallengeService(clock),
			new DeviceProofVerifier(),
			new DeviceTrustService(),
			new RefreshTokenService(clock, issuer, new AesGcmReplayPayloadProtector(new Services.Keys.Service.FileMasterSecretProvider(dir.OptionsFor("v1"))), new FakeSessionRevocationNotifier()),
			issuer);
	}

	private static WireContracts.DeviceProof SignProof(WireContracts.DeviceChallenge challenge, ECDsa key)
	{
		Services.Sessions.Models.DeviceProofBinding binding = new(
			challenge.ChallengeId, challenge.Purpose.ToString(), challenge.AccountContextId, challenge.DeviceRegistrationId,
			challenge.PublicKey.KeyId, DecodeBase64Url(challenge.PublicKey.Spki)!, DecodeBase64Url(challenge.Nonce)!, challenge.ExpiresAtUtc);
		byte[] signature = key.SignData(DeviceProofVerifier.BuildSignedBytes("chat-device-proof-v1", binding), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		return new WireContracts.DeviceProof(challenge.ChallengeId, Base64Url.Encode(signature));
	}

	private static async Task<int> CreateLegacyUserAsync(AppDbContext db)
	{
		UserEntity user = new($"Test {Guid.NewGuid():N}", $"+1555{Random.Shared.Next(1000000, 9999999)}");
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.Id;
	}

	[Fact]
	public async Task FirstDevice_EndToEnd_RequestChallengeThenRegister_IssuesWorkingSessionTokens()
	{
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		// Stage 06: a challenge is bound to the local copy it was issued for, and register must present
		// that same one.
		string localInstanceId = TestData.NewId("inst");

		var (spki, key) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");
		WireContracts.DeviceChallenge challenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki, localInstanceId);
		Assert.Equal(WireContracts.ChallengePurpose.Enrollment, challenge.Purpose);

		WireContracts.DeviceProof proof = SignProof(challenge, key);
		WireContracts.SessionTokens tokens = await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceId, proof, approval: null);

		Assert.NotNull(tokens.AccessToken);
		Assert.Equal(deviceId, tokens.Session.Device.DeviceRegistrationId);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var device = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == deviceId);
		Assert.Equal(Database.Enums.Sync.DeviceTrustState.Trusted, device.TrustState);
		Assert.True(device.IsFirstDevice);
	}

	[Fact]
	public async Task SecondDevice_WithoutApproval_IsForbidden()
	{
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		// Stage 06: a challenge is bound to the local copy it was issued for, and register must present
		// that same one.
		string localInstanceId = TestData.NewId("inst");

		var (spkiA, keyA) = TestData.NewDeviceKey();
		WireContracts.DeviceChallenge challengeA = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev"), "key-a", spkiA, localInstanceId);
		await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceId, SignProof(challengeA, keyA), null);

		var (spkiB, _) = TestData.NewDeviceKey();
		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev"), "key-b", spkiB, localInstanceId));
	}

	[Fact]
	public async Task SecondDevice_WithAValidApprovalFromTheFirstDevice_Succeeds()
	{
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		// Stage 06: a challenge is bound to the local copy it was issued for, and register must present
		// that same one.
		string localInstanceId = TestData.NewId("inst");
		string localInstanceIdB = TestData.NewId("inst-b");

		var (spkiA, keyA) = TestData.NewDeviceKey();
		string deviceAId = TestData.NewId("dev-a");
		WireContracts.DeviceChallenge challengeA = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceAId, "key-a", spkiA, localInstanceId);
		WireContracts.SessionTokens tokensA = await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceId, SignProof(challengeA, keyA), null);
		string accountId = tokensA.Session.Device.AccountContextId;

		var (spkiB, keyB) = TestData.NewDeviceKey();
		string deviceBId = TestData.NewId("dev-b");

		// Device A does not go through RequestEnrollmentChallengeAsync (that is for the enrolling
		// device itself); device B requests its own challenge, which the coordinator should mark as
		// requiring DeviceApproval since account already has a trusted device.
		WireContracts.DeviceChallenge challengeB = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceBId, "key-b", spkiB, localInstanceIdB);
		Assert.Equal(WireContracts.ChallengePurpose.DeviceApproval, challengeB.Purpose);

		// Device A signs an APPROVAL over device B's exact challenge fields.
		Services.Sessions.Models.DeviceProofBinding bindingForApproval = new(
			challengeB.ChallengeId, challengeB.Purpose.ToString(), challengeB.AccountContextId, challengeB.DeviceRegistrationId,
			challengeB.PublicKey.KeyId, DecodeBase64Url(challengeB.PublicKey.Spki)!, DecodeBase64Url(challengeB.Nonce)!, challengeB.ExpiresAtUtc);
		byte[] approvalSignature = keyA.SignData(
			DeviceProofVerifier.BuildSignedBytes("chat-device-approval-v1", bindingForApproval), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		// Stage 06: the approving device's own session must still be live at commit, so the approval
		// names device A's real session; every other claimed field stays deliberately bogus, because
		// none of them is authority (the server re-reads trust/liveness from its own rows).
		WireContracts.DeviceApproval approval = new(
			challengeB.ChallengeId,
			new WireContracts.SessionContext(
				new WireContracts.DeviceContext(accountId, deviceAId, "irrelevant-claimed-instance"),
				tokensA.Session.SessionId,
				"irrelevant-claimed-trust-version"),
			Base64Url.Encode(approvalSignature));

		WireContracts.SessionTokens tokensB = await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceIdB, SignProof(challengeB, keyB), approval);
		Assert.Equal(deviceBId, tokensB.Session.Device.DeviceRegistrationId);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var deviceB = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == deviceBId);
		Assert.Equal(deviceAId, deviceB.ApprovedByDeviceRegistrationId);
		Assert.False(deviceB.IsFirstDevice);
	}

	[Fact]
	public async Task SecondDevice_WithAnApprovalSignedByADeviceThatIsNotActuallyTrusted_IsForbidden()
	{
		// "supplied ApprovingSession is not authority" - the ApprovingSession claims a device id that
		// was never actually trusted for this account; the coordinator must look up ground truth, not
		// trust the claim.
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		// Stage 06: a challenge is bound to the local copy it was issued for, and register must present
		// that same one.
		string localInstanceId = TestData.NewId("inst");
		string localInstanceIdB = TestData.NewId("inst-b");

		var (spkiA, keyA) = TestData.NewDeviceKey();
		WireContracts.DeviceChallenge challengeA = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev-a"), "key-a", spkiA, localInstanceId);
		WireContracts.SessionTokens tokensA = await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceId, SignProof(challengeA, keyA), null);
		string accountId = tokensA.Session.Device.AccountContextId;

		var (spkiAttacker, keyAttacker) = TestData.NewDeviceKey(); // never registered/trusted at all
		var (spkiB, keyB) = TestData.NewDeviceKey();
		string deviceBId = TestData.NewId("dev-b");
		WireContracts.DeviceChallenge challengeB = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceBId, "key-b", spkiB, localInstanceIdB);

		Services.Sessions.Models.DeviceProofBinding bindingForApproval = new(
			challengeB.ChallengeId, challengeB.Purpose.ToString(), challengeB.AccountContextId, challengeB.DeviceRegistrationId,
			challengeB.PublicKey.KeyId, DecodeBase64Url(challengeB.PublicKey.Spki)!, DecodeBase64Url(challengeB.Nonce)!, challengeB.ExpiresAtUtc);
		byte[] forgedApprovalSignature = keyAttacker.SignData(
			DeviceProofVerifier.BuildSignedBytes("chat-device-approval-v1", bindingForApproval), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

		// Claims to be approved by a made-up device id that was never registered.
		WireContracts.DeviceApproval forgedApproval = new(
			challengeB.ChallengeId,
			new WireContracts.SessionContext(new WireContracts.DeviceContext(accountId, TestData.NewId("fake-approver"), "x"), "x", "1"),
			Base64Url.Encode(forgedApprovalSignature));

		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceIdB, SignProof(challengeB, keyB), forgedApproval));
	}

	[Fact]
	public async Task AlreadyTrustedDevice_WithADeadSessionAndNoApproval_ReauthenticatesWithJustItsOwnProof()
	{
		// "Known still-trusted device needs valid credential+proof reauthentication when its refresh
		// session expires/requires login" - a single trusted device must be able to recover from a dead
		// local session (e.g. its stored refresh token expired/was invalidated) WITHOUT another device's
		// approval, since none could ever exist to provide it (it is the only trusted device here).
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		// Stage 06: a challenge is bound to the local copy it was issued for, and register must present
		// that same one.
		string localInstanceId = TestData.NewId("inst");

		var (spki, key) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");
		WireContracts.DeviceChallenge firstChallenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki, localInstanceId);
		await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceId, SignProof(firstChallenge, key), approval: null);

		// Simulate "its refresh session expires/requires login" - the device's own key/registration
		// stays Trusted; only its session/local tokens are gone (same observable state as a user who
		// force-quit the app long enough for the refresh token to expire).
		WireContracts.DeviceChallenge secondChallenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki, localInstanceId);
		Assert.Equal(WireContracts.ChallengePurpose.Enrollment, secondChallenge.Purpose); // NOT DeviceApproval.

		WireContracts.SessionTokens reauthTokens = await coordinator.RegisterDeviceAsync(
			db, legacyUserId, localInstanceId, SignProof(secondChallenge, key), approval: null);
		Assert.Equal(deviceId, reauthTokens.Session.Device.DeviceRegistrationId);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var device = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == deviceId);
		Assert.Equal(Database.Enums.Sync.DeviceTrustState.Trusted, device.TrustState);
		Assert.True(device.IsFirstDevice); // untouched by the reauthentication path.
	}

	[Fact]
	public async Task RevokedDevice_ReusingItsOwnId_CannotReauthenticateAndStaysBehindTheApprovalBarrier()
	{
		// The mirror image of the reauthentication test above: "never change device/key identity to
		// escape revocation" - reusing the SAME DeviceRegistrationId/key after being revoked must never
		// be mistaken for the AlreadyTrustedReauthentication path.
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);
		DeviceTrustService trustService = new();

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		// Stage 06: a challenge is bound to the local copy it was issued for, and register must present
		// that same one.
		string localInstanceId = TestData.NewId("inst");

		var (spki, key) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");
		WireContracts.DeviceChallenge challenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki, localInstanceId);
		WireContracts.SessionTokens tokens = await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceId, SignProof(challenge, key), approval: null);
		string accountId = tokens.Session.Device.AccountContextId;

		var deviceBefore = await db.SyncDeviceRegistrations.AsNoTracking().SingleAsync(d => d.Id == deviceId);
		// actingSessionId is now required (no test-only bypass) - RegisterDeviceAsync above already
		// created a real, live session for this exact device, so its own SessionId is the correct one to
		// pass (self-revoke: acting == target).
		await trustService.RevokeDeviceAsync(db, accountId, deviceId, deviceBefore.TrustVersion, deviceId, DateTime.UtcNow, tokens.Session.SessionId);

		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki, localInstanceId));
	}

	[Fact]
	public async Task ChallengeIssuedBeforeAFreshStart_IsRefusedOutright_SoTheDeviceCanEnrollIntoTheNewContext()
	{
		// plan/06-recovery.md's "ново начало със същия номер", from the device that needed it: a second
		// device is told to get an approval it cannot get, so the user starts over. Its kept challenge
		// belongs to the context that just ended; registering it must be REFUSED (401 - the client then
		// drops the record and asks for a fresh challenge), not answered "another device must approve",
		// which would strand the device retrying the old context forever.
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);
		SyncAccountProvisioningService provisioning = new();

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		string localInstanceIdA = TestData.NewId("inst-a");
		string localInstanceIdB = TestData.NewId("inst-b");

		var (spkiA, keyA) = TestData.NewDeviceKey();
		WireContracts.DeviceChallenge challengeA = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev-a"), "key-a", spkiA, localInstanceIdA);
		await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceIdA, SignProof(challengeA, keyA), approval: null);

		var (spkiB, keyB) = TestData.NewDeviceKey();
		string deviceBId = TestData.NewId("dev-b");
		WireContracts.DeviceChallenge staleChallenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceBId, "key-b", spkiB, localInstanceIdB);
		Assert.Equal(WireContracts.ChallengePurpose.DeviceApproval, staleChallenge.Purpose);

		await provisioning.StartNewAccountContextAsync(db, legacyUserId, "fresh-start-op-1", clock.GetUtcNow().UtcDateTime);

		// From here on a fresh DbContext, as every request has its own (the pattern FreshStartBoundaryTests
		// uses for the same reason - the ended row must be re-read, not served from the change tracker).
		await using AppDbContext afterDb = fixture.CreateContext();

		await Assert.ThrowsAsync<SessionNotTrustedException>(() =>
			coordinator.RegisterDeviceAsync(afterDb, legacyUserId, localInstanceIdB, SignProof(staleChallenge, keyB), approval: null));

		// And the same phone enrolls into the successor as what it now is: a NEW registration of a NEW
		// local copy (Crypto/DeviceKeyContext.startNewDeviceRegistration / LocalInstanceIdentity.
		// startNewLocalInstanceId), which the empty new context takes as its genuine first device - no
		// approval exists there to wait for.
		var (spkiB2, keyB2) = TestData.NewDeviceKey();
		string deviceB2Id = TestData.NewId("dev-b2");
		string localInstanceIdB2 = TestData.NewId("inst-b2");
		WireContracts.DeviceChallenge freshChallenge = await coordinator.RequestEnrollmentChallengeAsync(afterDb, legacyUserId, deviceB2Id, "key-b2", spkiB2, localInstanceIdB2);
		Assert.Equal(WireContracts.ChallengePurpose.Enrollment, freshChallenge.Purpose);
		Assert.NotEqual(staleChallenge.AccountContextId, freshChallenge.AccountContextId);

		WireContracts.SessionTokens tokensB2 = await coordinator.RegisterDeviceAsync(afterDb, legacyUserId, localInstanceIdB2, SignProof(freshChallenge, keyB2), approval: null);
		Assert.Equal(freshChallenge.AccountContextId, tokensB2.Session.Device.AccountContextId);

		// The registration it left behind is retained, untouched, in the context that ended - nothing is
		// rebound to the new context.
		await using AppDbContext verifyDb = fixture.CreateContext();
		var oldRegistration = await verifyDb.SyncDeviceRegistrations.AsNoTracking().SingleAsync(d => d.Id == deviceBId);
		Assert.Equal(staleChallenge.AccountContextId, oldRegistration.AccountId);
	}

	[Fact]
	public async Task RegistrationIdOfAContextThisIdentityReplaced_IsRefusedAsSuperseded()
	{
		// A DeviceRegistrationId is unique across the whole server (PK on the id alone), so it belongs to
		// exactly one account context. An install that kept the id of the context a fresh start replaced
		// must be told exactly that (409 -> it mints new ids and enrolls into the current context), never
		// answered with a constraint error, and never with a refusal it could confuse with revocation.
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);
		SyncAccountProvisioningService provisioning = new();

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		string localInstanceId = TestData.NewId("inst");

		var (spki, key) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");
		WireContracts.DeviceChallenge challenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki, localInstanceId);
		await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceId, SignProof(challenge, key), approval: null);

		await provisioning.StartNewAccountContextAsync(db, legacyUserId, "fresh-start-op-2", clock.GetUtcNow().UtcDateTime);

		await using AppDbContext afterDb = fixture.CreateContext();
		await Assert.ThrowsAsync<DeviceIdentitySupersededException>(() =>
			coordinator.RequestEnrollmentChallengeAsync(afterDb, legacyUserId, deviceId, "key-1", spki, TestData.NewId("inst-2")));
	}

	[Fact]
	public async Task RegistrationIdOfAnotherIdentitysContext_StaysTheUninformativeRefusal()
	{
		// The superseded answer is a statement about the caller's OWN identity. Someone else's id - live
		// context or ended - must stay indistinguishable from an unknown one, so it can never be used to
		// probe other accounts or to earn an identity rotation.
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);
		SyncAccountProvisioningService provisioning = new();

		await using AppDbContext db = fixture.CreateContext();
		int otherUserId = await CreateLegacyUserAsync(db);
		var (otherSpki, otherKey) = TestData.NewDeviceKey();
		string otherDeviceId = TestData.NewId("dev-other");
		string otherInstanceId = TestData.NewId("inst-other");
		WireContracts.DeviceChallenge otherChallenge = await coordinator.RequestEnrollmentChallengeAsync(db, otherUserId, otherDeviceId, "key-other", otherSpki, otherInstanceId);
		await coordinator.RegisterDeviceAsync(db, otherUserId, otherInstanceId, SignProof(otherChallenge, otherKey), approval: null);
		// Even ended, that context is not this caller's.
		await provisioning.StartNewAccountContextAsync(db, otherUserId, "fresh-start-op-other", clock.GetUtcNow().UtcDateTime);

		await using AppDbContext afterDb = fixture.CreateContext();
		int myUserId = await CreateLegacyUserAsync(afterDb);
		var (mySpki, _) = TestData.NewDeviceKey();
		await Assert.ThrowsAsync<SessionNotTrustedException>(() =>
			coordinator.RequestEnrollmentChallengeAsync(afterDb, myUserId, otherDeviceId, "key-mine", mySpki, TestData.NewId("inst-mine")));
	}

	[Fact]
	public async Task ASiblingInstallJoinsTheSuccessor_UnderThatContextsOwnTrustRules()
	{
		// The case the superseded answer exists for: install A performed the fresh start and is already
		// trusted in the successor; install B still holds the ended context's ids. B must be able to join
		// the SAME successor without a second fresh start - and strictly under the successor's own rules,
		// i.e. as an approval-required registration, never as a free first device.
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);
		SyncAccountProvisioningService provisioning = new();

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);

		// Both installs are enrolled in the old context.
		var (spkiA, keyA) = TestData.NewDeviceKey();
		string instanceA = TestData.NewId("inst-a");
		WireContracts.DeviceChallenge challengeA = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev-a"), "key-a", spkiA, instanceA);
		await coordinator.RegisterDeviceAsync(db, legacyUserId, instanceA, SignProof(challengeA, keyA), approval: null);

		// Install B is known to the old context too (a registration waiting for approval - the very
		// state the fresh start exists for).
		var (spkiB, _) = TestData.NewDeviceKey();
		string deviceB = TestData.NewId("dev-b");
		string instanceB = TestData.NewId("inst-b");
		WireContracts.DeviceChallenge challengeB = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceB, "key-b", spkiB, instanceB);
		Assert.Equal(WireContracts.ChallengePurpose.DeviceApproval, challengeB.Purpose);

		// Install A starts over and enrolls into the successor as its first device.
		Database.Entities.Sync.SyncAccountEntity successor = await provisioning.StartNewAccountContextAsync(db, legacyUserId, "fresh-start-op-sibling", clock.GetUtcNow().UtcDateTime);
		await using AppDbContext afterDb = fixture.CreateContext();
		var (spkiA2, keyA2) = TestData.NewDeviceKey();
		string instanceA2 = TestData.NewId("inst-a2");
		WireContracts.DeviceChallenge challengeA2 = await coordinator.RequestEnrollmentChallengeAsync(afterDb, legacyUserId, TestData.NewId("dev-a2"), "key-a2", spkiA2, instanceA2);
		await coordinator.RegisterDeviceAsync(afterDb, legacyUserId, instanceA2, SignProof(challengeA2, keyA2), approval: null);

		// Install B presents what it still holds and is told the exact reason.
		await using AppDbContext siblingDb = fixture.CreateContext();
		await Assert.ThrowsAsync<DeviceIdentitySupersededException>(() =>
			coordinator.RequestEnrollmentChallengeAsync(siblingDb, legacyUserId, deviceB, "key-b", spkiB, instanceB));

		// Its scoped transition: new registration id, new copy id - and then the successor's own rules
		// decide, which here means another trusted device must approve. No bypass, no second fresh start.
		await using AppDbContext joinDb = fixture.CreateContext();
		var (spkiB2, _) = TestData.NewDeviceKey();
		WireContracts.DeviceChallenge joinChallenge = await coordinator.RequestEnrollmentChallengeAsync(
			joinDb, legacyUserId, TestData.NewId("dev-b2"), "key-b2", spkiB2, TestData.NewId("inst-b2"));

		Assert.Equal(successor.Id, joinChallenge.AccountContextId);
		Assert.Equal(WireContracts.ChallengePurpose.DeviceApproval, joinChallenge.Purpose);
	}

	[Fact]
	public async Task LocalInstanceIdOfAContextThisIdentityReplaced_IsRefusedAsSuperseded()
	{
		// The same rule one level down: a LocalInstanceId names one SQLite copy and is likewise unique
		// server-side, so a new registration may not claim the copy id an earlier registration recorded.
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);
		SyncAccountProvisioningService provisioning = new();

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);
		string localInstanceId = TestData.NewId("inst");

		var (spki, key) = TestData.NewDeviceKey();
		WireContracts.DeviceChallenge challenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev"), "key-1", spki, localInstanceId);
		await coordinator.RegisterDeviceAsync(db, legacyUserId, localInstanceId, SignProof(challenge, key), approval: null);

		await provisioning.StartNewAccountContextAsync(db, legacyUserId, "fresh-start-op-3", clock.GetUtcNow().UtcDateTime);

		// New registration (as a fresh start mints), but the copy id of the context just left.
		await using AppDbContext afterDb = fixture.CreateContext();
		var (spki2, key2) = TestData.NewDeviceKey();
		WireContracts.DeviceChallenge challenge2 = await coordinator.RequestEnrollmentChallengeAsync(afterDb, legacyUserId, TestData.NewId("dev-2"), "key-2", spki2, localInstanceId);
		await Assert.ThrowsAsync<DeviceIdentitySupersededException>(() =>
			coordinator.RegisterDeviceAsync(afterDb, legacyUserId, localInstanceId, SignProof(challenge2, key2), approval: null));
	}
}
