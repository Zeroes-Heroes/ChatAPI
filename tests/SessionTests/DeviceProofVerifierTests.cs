using System.Security.Cryptography;
using Services.Sessions;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Xunit;

namespace SessionTests;

/// <summary>Pure-crypto tests for the chat-device-proof-v1/chat-device-approval-v1 signature scheme - no database needed.</summary>
public class DeviceProofVerifierTests
{
	private readonly DeviceProofVerifier _verifier = new();

	private static DeviceProofBinding MakeBinding(string purpose = "Enrollment") => new(
		ChallengeId: TestData.NewId("chal"),
		Purpose: purpose,
		AccountContextId: TestData.NewId("acct"),
		DeviceRegistrationId: TestData.NewId("dev"),
		KeyId: TestData.NewId("key"),
		Spki: TestData.NewDeviceKey().Spki,
		Nonce: RandomNumberGenerator.GetBytes(32),
		ExpiresAtUtc: RfcTimestamp.Format(DateTime.UtcNow.AddMinutes(5)));

	private static byte[] Sign(string domain, DeviceProofBinding binding, ECDsa key) =>
		key.SignData(DeviceProofVerifier.BuildSignedBytes(domain, binding), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

	[Fact]
	public void VerifyDeviceProof_AcceptsAValidSignature()
	{
		var (spki, key) = TestData.NewDeviceKey();
		DeviceProofBinding binding = MakeBinding() with { Spki = spki };
		byte[] signature = Sign("chat-device-proof-v1", binding, key);

		_verifier.VerifyDeviceProof(binding, spki, signature);
	}

	[Fact]
	public void VerifyDeviceProof_RejectsAWrongKey()
	{
		var (spki, _) = TestData.NewDeviceKey();
		var (_, otherKey) = TestData.NewDeviceKey();
		DeviceProofBinding binding = MakeBinding() with { Spki = spki };
		byte[] signature = Sign("chat-device-proof-v1", binding, otherKey);

		Assert.Throws<DeviceProofInvalidException>(() => _verifier.VerifyDeviceProof(binding, spki, signature));
	}

	[Fact]
	public void VerifyDeviceProof_RejectsATamperedBindingField()
	{
		var (spki, key) = TestData.NewDeviceKey();
		DeviceProofBinding binding = MakeBinding() with { Spki = spki };
		byte[] signature = Sign("chat-device-proof-v1", binding, key);

		DeviceProofBinding tampered = binding with { AccountContextId = TestData.NewId("acct-attacker") };
		Assert.Throws<DeviceProofInvalidException>(() => _verifier.VerifyDeviceProof(tampered, spki, signature));
	}

	[Fact]
	public void VerifyDeviceProof_RejectsASignatureProducedForTheWrongDomain()
	{
		// A chat-device-approval-v1 signature must never verify as a chat-device-proof-v1 signature
		// for the same fields, even though every other field matches exactly.
		var (spki, key) = TestData.NewDeviceKey();
		DeviceProofBinding binding = MakeBinding() with { Spki = spki };
		byte[] approvalSignature = Sign("chat-device-approval-v1", binding, key);

		Assert.Throws<DeviceProofInvalidException>(() => _verifier.VerifyDeviceProof(binding, spki, approvalSignature));
	}

	[Fact]
	public void VerifyDeviceApproval_AcceptsAValidApprovalSignature()
	{
		var (spki, key) = TestData.NewDeviceKey();
		DeviceProofBinding binding = MakeBinding("DeviceApproval") with { Spki = spki };
		byte[] signature = Sign("chat-device-approval-v1", binding, key);

		_verifier.VerifyDeviceApproval(binding, spki, signature);
	}

	[Fact]
	public void VerifyDeviceProof_RejectsAWrongLengthSignature()
	{
		var (spki, _) = TestData.NewDeviceKey();
		DeviceProofBinding binding = MakeBinding() with { Spki = spki };
		Assert.Throws<DeviceProofInvalidException>(() => _verifier.VerifyDeviceProof(binding, spki, new byte[63]));
	}

	[Fact]
	public void VerifyDeviceProof_RejectsANonP256Key()
	{
		using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
		byte[] spki384 = p384.ExportSubjectPublicKeyInfo();
		DeviceProofBinding binding = MakeBinding() with { Spki = spki384 };
		byte[] signature = p384.SignData(
			DeviceProofVerifier.BuildSignedBytes("chat-device-proof-v1", binding), HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

		Assert.Throws<DeviceProofInvalidException>(() => _verifier.VerifyDeviceProof(binding, spki384, signature));
	}
}
