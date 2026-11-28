using System.Security.Cryptography;
using Database.Context;
using Microsoft.Extensions.Options;
using Services.Crypto;
using Services.Sessions.Service;
using Services.Utilities.Models;
using WireContracts = Services.Sync.Contracts;
using Xunit;

namespace SessionTests;

[Collection(DatabaseCollection.Name)]
public class SessionRefreshCoordinatorTests(DatabaseFixture fixture)
{
	private static byte[] DecodeBase64Url(string value) => Base64Url.TryDecode(value, out byte[] bytes) ? bytes : throw new FormatException("Invalid base64url test data");
	private static SessionRefreshCoordinator MakeCoordinator(TestMasterSecretDirectory dir, FakeTimeProvider clock)
	{
		dir.WriteVersion("v1");
		SessionTokenIssuer issuer = new(Options.Create(new AppSettings { TokenIssuer = "test-issuer", TokenAudience = "test-audience", TokenSigningKey = new string('k', 64) }));
		return new SessionRefreshCoordinator(
			new DeviceChallengeService(clock),
			new DeviceProofVerifier(),
			new RefreshTokenService(clock, issuer, new AesGcmReplayPayloadProtector(new Services.Keys.Service.FileMasterSecretProvider(dir.OptionsFor("v1"))), new FakeSessionRevocationNotifier()));
	}

	private static WireContracts.DeviceProof SignProof(WireContracts.DeviceChallenge challenge, ECDsa key)
	{
		Services.Sessions.Models.DeviceProofBinding binding = new(
			challenge.ChallengeId, challenge.Purpose.ToString(), challenge.AccountContextId, challenge.DeviceRegistrationId,
			challenge.PublicKey.KeyId, DecodeBase64Url(challenge.PublicKey.Spki)!, DecodeBase64Url(challenge.Nonce)!, challenge.ExpiresAtUtc);
		byte[] signature = key.SignData(DeviceProofVerifier.BuildSignedBytes("chat-device-proof-v1", binding), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		return new WireContracts.DeviceProof(challenge.ChallengeId, Base64Url.Encode(signature));
	}

	[Fact]
	public async Task EndToEnd_ChallengeThenRefresh_Succeeds()
	{
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		SessionRefreshCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, key) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		string operationId = TestData.NewId("op");
		WireContracts.DeviceChallenge challenge = await coordinator.RequestSessionBindingChallengeAsync(db, session.Id, operationId);
		Assert.Equal(WireContracts.ChallengePurpose.SessionBinding, challenge.Purpose);

		WireContracts.RefreshSessionRequest request = new(session.Id, operationId, "raw-token-1", SignProof(challenge, key));
		WireContracts.RefreshSessionResult result = await coordinator.RefreshAsync(db, request);

		Assert.Equal(WireContracts.RefreshOutcome.Rotated, result.Outcome);
		Assert.NotNull(result.Tokens);
	}

	[Fact]
	public async Task Refresh_WithAChallengeIssuedForADifferentOperation_IsRejected()
	{
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		SessionRefreshCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, key) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		WireContracts.DeviceChallenge challenge = await coordinator.RequestSessionBindingChallengeAsync(db, session.Id, TestData.NewId("op-original"));

		// Submits a refresh under a DIFFERENT operation id than the one the challenge was bound to.
		WireContracts.RefreshSessionRequest request = new(session.Id, TestData.NewId("op-different"), "raw-token-1", SignProof(challenge, key));
		WireContracts.RefreshSessionResult result = await coordinator.RefreshAsync(db, request);

		Assert.Equal(WireContracts.RefreshOutcome.ReauthenticationRequired, result.Outcome);
	}

	[Fact]
	public async Task Refresh_WithAProofSignedByTheWrongKey_IsRejected()
	{
		using var dir = new TestMasterSecretDirectory();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		SessionRefreshCoordinator coordinator = MakeCoordinator(dir, clock);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var (_, attackerKey) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		string operationId = TestData.NewId("op");
		WireContracts.DeviceChallenge challenge = await coordinator.RequestSessionBindingChallengeAsync(db, session.Id, operationId);
		WireContracts.RefreshSessionRequest request = new(session.Id, operationId, "raw-token-1", SignProof(challenge, attackerKey));

		WireContracts.RefreshSessionResult result = await coordinator.RefreshAsync(db, request);
		Assert.Equal(WireContracts.RefreshOutcome.ReauthenticationRequired, result.Outcome);
	}
}
