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

		var (spki, key) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");
		WireContracts.DeviceChallenge challenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki);
		Assert.Equal(WireContracts.ChallengePurpose.Enrollment, challenge.Purpose);

		WireContracts.DeviceProof proof = SignProof(challenge, key);
		WireContracts.SessionTokens tokens = await coordinator.RegisterDeviceAsync(db, legacyUserId, TestData.NewId("inst"), proof, approval: null);

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

		var (spkiA, keyA) = TestData.NewDeviceKey();
		WireContracts.DeviceChallenge challengeA = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev"), "key-a", spkiA);
		await coordinator.RegisterDeviceAsync(db, legacyUserId, TestData.NewId("inst"), SignProof(challengeA, keyA), null);

		var (spkiB, _) = TestData.NewDeviceKey();
		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev"), "key-b", spkiB));
	}

	[Fact]
	public async Task SecondDevice_WithAValidApprovalFromTheFirstDevice_Succeeds()
	{
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceEnrollmentCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		int legacyUserId = await CreateLegacyUserAsync(db);

		var (spkiA, keyA) = TestData.NewDeviceKey();
		string deviceAId = TestData.NewId("dev-a");
		WireContracts.DeviceChallenge challengeA = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceAId, "key-a", spkiA);
		WireContracts.SessionTokens tokensA = await coordinator.RegisterDeviceAsync(db, legacyUserId, TestData.NewId("inst-a"), SignProof(challengeA, keyA), null);
		string accountId = tokensA.Session.Device.AccountContextId;

		var (spkiB, keyB) = TestData.NewDeviceKey();
		string deviceBId = TestData.NewId("dev-b");

		// Device A does not go through RequestEnrollmentChallengeAsync (that is for the enrolling
		// device itself); device B requests its own challenge, which the coordinator should mark as
		// requiring DeviceApproval since account already has a trusted device.
		WireContracts.DeviceChallenge challengeB = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceBId, "key-b", spkiB);
		Assert.Equal(WireContracts.ChallengePurpose.DeviceApproval, challengeB.Purpose);

		// Device A signs an APPROVAL over device B's exact challenge fields.
		Services.Sessions.Models.DeviceProofBinding bindingForApproval = new(
			challengeB.ChallengeId, challengeB.Purpose.ToString(), challengeB.AccountContextId, challengeB.DeviceRegistrationId,
			challengeB.PublicKey.KeyId, DecodeBase64Url(challengeB.PublicKey.Spki)!, DecodeBase64Url(challengeB.Nonce)!, challengeB.ExpiresAtUtc);
		byte[] approvalSignature = keyA.SignData(
			DeviceProofVerifier.BuildSignedBytes("chat-device-approval-v1", bindingForApproval), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		WireContracts.DeviceApproval approval = new(
			challengeB.ChallengeId,
			new WireContracts.SessionContext(new WireContracts.DeviceContext(accountId, deviceAId, "irrelevant-claimed-instance"), "irrelevant-claimed-session", "irrelevant-claimed-trust-version"),
			Base64Url.Encode(approvalSignature));

		WireContracts.SessionTokens tokensB = await coordinator.RegisterDeviceAsync(db, legacyUserId, TestData.NewId("inst-b"), SignProof(challengeB, keyB), approval);
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

		var (spkiA, keyA) = TestData.NewDeviceKey();
		WireContracts.DeviceChallenge challengeA = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, TestData.NewId("dev-a"), "key-a", spkiA);
		WireContracts.SessionTokens tokensA = await coordinator.RegisterDeviceAsync(db, legacyUserId, TestData.NewId("inst-a"), SignProof(challengeA, keyA), null);
		string accountId = tokensA.Session.Device.AccountContextId;

		var (spkiAttacker, keyAttacker) = TestData.NewDeviceKey(); // never registered/trusted at all
		var (spkiB, keyB) = TestData.NewDeviceKey();
		string deviceBId = TestData.NewId("dev-b");
		WireContracts.DeviceChallenge challengeB = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceBId, "key-b", spkiB);

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
			coordinator.RegisterDeviceAsync(db, legacyUserId, TestData.NewId("inst-b"), SignProof(challengeB, keyB), forgedApproval));
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

		var (spki, key) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");
		WireContracts.DeviceChallenge firstChallenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki);
		await coordinator.RegisterDeviceAsync(db, legacyUserId, TestData.NewId("inst-1"), SignProof(firstChallenge, key), approval: null);

		// Simulate "its refresh session expires/requires login" - the device's own key/registration
		// stays Trusted; only its session/local tokens are gone (same observable state as a user who
		// force-quit the app long enough for the refresh token to expire).
		WireContracts.DeviceChallenge secondChallenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki);
		Assert.Equal(WireContracts.ChallengePurpose.Enrollment, secondChallenge.Purpose); // NOT DeviceApproval.

		WireContracts.SessionTokens reauthTokens = await coordinator.RegisterDeviceAsync(
			db, legacyUserId, TestData.NewId("inst-2"), SignProof(secondChallenge, key), approval: null);
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

		var (spki, key) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");
		WireContracts.DeviceChallenge challenge = await coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki);
		WireContracts.SessionTokens tokens = await coordinator.RegisterDeviceAsync(db, legacyUserId, TestData.NewId("inst-1"), SignProof(challenge, key), approval: null);
		string accountId = tokens.Session.Device.AccountContextId;

		var deviceBefore = await db.SyncDeviceRegistrations.AsNoTracking().SingleAsync(d => d.Id == deviceId);
		// actingSessionId is now required (no test-only bypass) - RegisterDeviceAsync above already
		// created a real, live session for this exact device, so its own SessionId is the correct one to
		// pass (self-revoke: acting == target).
		await trustService.RevokeDeviceAsync(db, accountId, deviceId, deviceBefore.TrustVersion, deviceId, DateTime.UtcNow, tokens.Session.SessionId);

		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			coordinator.RequestEnrollmentChallengeAsync(db, legacyUserId, deviceId, "key-1", spki));
	}
}
