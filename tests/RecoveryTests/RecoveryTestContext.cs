using System.Security.Claims;
using System.Security.Cryptography;
using Database.Context;
using Database.Entities;
using Microsoft.Extensions.Options;
using Services.Crypto;
using Services.Keys.Interface;
using Services.Keys.Service;
using Services.Recovery.Interface;
using Services.Recovery.Service;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Sync.Contracts;
using Services.Utilities.Models;
using WireContracts = Services.Sync.Contracts;

namespace RecoveryTests;

/// <summary>
/// Composes the REAL stage 04/05/06 services this suite drives (no stand-ins beyond the clock, the
/// mounted master-secret directory and the revocation-notifier sink), plus the signing/claims helpers
/// every recovery test needs.
/// </summary>
internal sealed class RecoveryTestContext : IDisposable
{
	public static byte[] DecodeBase64Url(string value) =>
		Base64Url.TryDecode(value, out byte[] bytes) ? bytes : throw new FormatException("Invalid base64url test data");

	public TestMasterSecretDirectory MasterSecrets { get; } = new();
	public FakeTimeProvider Clock { get; }
	public DeviceChallengeService Challenges { get; }
	public DeviceProofVerifier Proofs { get; } = new();
	public DeviceTrustService Trust { get; } = new();
	public DeviceApprovalService Approvals { get; }
	public SyncAccountProvisioningService Accounts { get; } = new();
	public IMessageKeyProtector Protector { get; }

	public RecoveryTestContext(DateTimeOffset? now = null)
	{
		Clock = new FakeTimeProvider(now ?? DateTimeOffset.UtcNow);
		MasterSecrets.WriteVersion("v1");
		Challenges = new DeviceChallengeService(Clock);
		Approvals = new DeviceApprovalService(Proofs);
		Protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(MasterSecrets.OptionsFor("v1")));
	}

	public IRecoveryEnvelopeService Envelopes(AppDbContext db) =>
		new RecoveryEnvelopeService(Protector, new SessionTrustChecker(db, Clock));

	public IRecoveryEnrollmentCoordinator RecoveryEnrollment(AppDbContext db)
	{
		SessionTokenIssuer issuer = new(Options.Create(new AppSettings
		{
			TokenIssuer = "test-issuer",
			TokenAudience = "test-audience",
			TokenSigningKey = new string('k', 64),
		}));
		return new RecoveryEnrollmentCoordinator(
			Clock,
			Accounts,
			Envelopes(db),
			Challenges,
			Proofs,
			Trust,
			new RefreshTokenService(Clock, issuer, new AesGcmReplayPayloadProtector(new FileMasterSecretProvider(MasterSecrets.OptionsFor("v1"))), new FakeSessionRevocationNotifier()),
			issuer);
	}

	/// <summary>The stage-05 enrollment coordinator, for the approval-grant consumption path.</summary>
	public DeviceEnrollmentCoordinator Enrollment()
	{
		SessionTokenIssuer issuer = new(Options.Create(new AppSettings
		{
			TokenIssuer = "test-issuer",
			TokenAudience = "test-audience",
			TokenSigningKey = new string('k', 64),
		}));
		return new DeviceEnrollmentCoordinator(
			Clock,
			Accounts,
			Challenges,
			Proofs,
			Trust,
			new RefreshTokenService(Clock, issuer, new AesGcmReplayPayloadProtector(new FileMasterSecretProvider(MasterSecrets.OptionsFor("v1"))), new FakeSessionRevocationNotifier()),
			issuer);
	}

	public static async Task<int> CreateLegacyUserAsync(AppDbContext db)
	{
		UserEntity user = new($"Test {Guid.NewGuid():N}", $"+1555{Random.Shared.Next(1000000, 9999999)}");
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.Id;
	}

	/// <summary>The claims an app-session token carries, as <see cref="SessionTrustChecker"/> reads them - not a stand-in principal with convenient values.</summary>
	public ClaimsPrincipal AppSessionPrincipal(int legacyUserId, string accountId, string sessionId, string deviceId, string localInstanceId, long trustVersion) =>
		new(new ClaimsIdentity(
		[
			new Claim(ClaimTypes.NameIdentifier, legacyUserId.ToString()),
			new Claim(SessionClaimTypes.TokenPurpose, SessionClaimTypes.AppPurpose),
			new Claim(SessionClaimTypes.AccountContextId, accountId),
			new Claim(SessionClaimTypes.SessionId, sessionId),
			new Claim(SessionClaimTypes.DeviceRegistrationId, deviceId),
			new Claim(SessionClaimTypes.LocalInstanceId, localInstanceId),
			new Claim(SessionClaimTypes.TrustVersion, trustVersion.ToString()),
			new Claim("exp", Clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds().ToString()),
		], "test"));

	public static DeviceProofBinding BindingFor(WireContracts.DeviceChallenge challenge) =>
		new(challenge.ChallengeId, challenge.Purpose.ToString(), challenge.AccountContextId, challenge.DeviceRegistrationId,
			challenge.PublicKey.KeyId, RecoveryTestContext.DecodeBase64Url(challenge.PublicKey.Spki)!, RecoveryTestContext.DecodeBase64Url(challenge.Nonce)!, challenge.ExpiresAtUtc);

	public static WireContracts.DeviceProof Sign(string domain, WireContracts.DeviceChallenge challenge, ECDsa key)
	{
		byte[] signature = key.SignData(
			DeviceProofVerifier.BuildSignedBytes(domain, BindingFor(challenge)),
			HashAlgorithmName.SHA256,
			DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		return new WireContracts.DeviceProof(challenge.ChallengeId, Base64Url.Encode(signature));
	}

	/// <summary>A client-shaped recovery envelope: AES-256-GCM under a fixed "derived" key with the pinned chat-recovery-v1 AAD. Argon2id itself is a device-side integration check (see the stage report).</summary>
	public static (StoreRecoveryEnvelopeRequest Request, byte[] DerivedKey, ECDsa RecoveryKey) BuildEnvelope(
		string accountContextId, string version, byte[]? derivedKey = null, ECDsa? recoveryKey = null)
	{
		byte[] key = derivedKey ?? RandomNumberGenerator.GetBytes(32);
		ECDsa verification = recoveryKey ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
		RecoveryKdf kdf = new(
			"argon2id",
			Base64Url.Encode(RandomNumberGenerator.GetBytes(SyncProtocol.RecoveryKdfSaltBytes)),
			SyncProtocol.RecoveryKdfCandidateMemoryKiB,
			SyncProtocol.RecoveryKdfCandidateIterations,
			SyncProtocol.RecoveryKdfCandidateParallelism);

		byte[] aad = Services.Recovery.RecoveryAad.Build(
			accountContextId, long.Parse(version),
			Services.Recovery.Models.RecoveryKdfMetadata.FromWire(kdf));

		byte[] plaintext = System.Text.Encoding.UTF8.GetBytes($"{{\"accountContextId\":\"{accountContextId}\",\"authorKeys\":[]}}");
		byte[] nonce = RandomNumberGenerator.GetBytes(12);
		byte[] ciphertext = new byte[plaintext.Length];
		byte[] tag = new byte[16];
		using (AesGcm aes = new(key, 16))
		{
			aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
		}

		StoreRecoveryEnvelopeRequest request = new(
			SyncProtocol.Version,
			version,
			kdf,
			new EncryptedContent(Base64Url.Encode(nonce), Base64Url.Encode(ciphertext), Base64Url.Encode(tag)),
			Base64Url.Encode(verification.ExportSubjectPublicKeyInfo()));

		return (request, key, verification);
	}

	public void Dispose() => MasterSecrets.Dispose();
}
