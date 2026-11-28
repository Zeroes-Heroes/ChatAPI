using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Services.Crypto;
using Services.Crypto.Service;
using Services.Sync.Contracts;
using Xunit;

namespace CryptoTests;

public class MessagePackageVerifierTests
{
	private static MessageBinding DefaultBinding() => new(
		SyncProtocol.Version,
		new OperationIdentity(new DeviceContext("acct-1", "dev-1", "inst-1"), "op-1"),
		"chat-1",
		new AuthorKeyReference("acct-1", "chat-1", "authkey-1", "1"),
		OperationKind.SendText);

	private static EncryptedContent DefaultContent() => new(
		Base64Url.Encode(new byte[12]),
		Base64Url.Encode(new byte[5]),
		Base64Url.Encode(new byte[16]));

	private static (string Compact, byte[] Spki, MessageBinding Binding) BuildSigned(MessageBinding? binding = null, EncryptedContent? content = null)
	{
		MessageBinding b = binding ?? DefaultBinding();
		EncryptedContent c = content ?? DefaultContent();
		var payload = new SignedMessagePayload(b, c);
		byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, SyncProtocol.CreateJsonOptions());

		using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		string compact = TestJwsBuilder.Build(ecdsa, b.AuthorKey.AuthorKeyId, payloadBytes);
		byte[] spki = ecdsa.ExportSubjectPublicKeyInfo();
		return (compact, spki, b);
	}

	[Fact]
	public void Verify_ValidPackage_ReturnsVerifiedResult()
	{
		(string compact, byte[] spki, MessageBinding binding) = BuildSigned();
		var verifier = new MessagePackageVerifier();
		var verified = verifier.Verify(compact, binding, spki);

		Assert.Equal(binding, verified.Binding);
		Assert.Equal(12, verified.NonceBytes.Length);
		Assert.Equal(16, verified.TagBytes.Length);
		Assert.Equal(5, verified.CiphertextBytes.Length);
		Assert.Equal(verifier.ComputeDigest(compact), verified.PackageDigest);
	}

	[Fact]
	public void Verify_RejectsWrongPublicKey()
	{
		(string compact, _, MessageBinding binding) = BuildSigned();
		using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsSignatureException>(() => verifier.Verify(compact, binding, other.ExportSubjectPublicKeyInfo()));
	}

	[Fact]
	public void Verify_RejectsTamperedSignature()
	{
		(string compact, byte[] spki, MessageBinding binding) = BuildSigned();
		string[] segments = compact.Split('.');
		Assert.True(Base64Url.TryDecode(segments[2], out byte[] sig));
		sig[0] ^= 0xFF;
		string tampered = $"{segments[0]}.{segments[1]}.{Base64Url.Encode(sig)}";

		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsSignatureException>(() => verifier.Verify(tampered, binding, spki));
	}

	[Fact]
	public void Verify_RejectsTamperedCiphertext_ViaSignatureFailure()
	{
		// The signature covers the whole payload, which embeds EncryptedContent - tampering the
		// ciphertext after signing breaks the signature, per docs/sync-contract-v1.md ("Preserve
		// original signed bytes").
		(string compact, byte[] spki, MessageBinding binding) = BuildSigned();
		string[] segments = compact.Split('.');
		Assert.True(Base64Url.TryDecode(segments[1], out byte[] payloadBytes));
		string payloadJson = Encoding.UTF8.GetString(payloadBytes);
		string tamperedJson = payloadJson.Replace(DefaultContent().Ciphertext, Base64Url.Encode(new byte[] { 9, 9, 9, 9, 9 }));
		string tamperedPayloadSegment = Base64Url.Encode(Encoding.UTF8.GetBytes(tamperedJson));
		string tampered = $"{segments[0]}.{tamperedPayloadSegment}.{segments[2]}";

		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsSignatureException>(() => verifier.Verify(tampered, binding, spki));
	}

	[Theory]
	[MemberData(nameof(MismatchedBindingCases))]
	public void Verify_RejectsBindingMismatch(MessageBinding expected)
	{
		(string compact, byte[] spki, _) = BuildSigned();
		var verifier = new MessagePackageVerifier();
		Assert.Throws<MessageBindingMismatchException>(() => verifier.Verify(compact, expected, spki));
	}

	public static IEnumerable<object[]> MismatchedBindingCases()
	{
		MessageBinding b = DefaultBinding();
		yield return new object[] { b with { ChatId = "different-chat" } };
		yield return new object[] { b with { Operation = b.Operation with { Device = b.Operation.Device with { AccountContextId = "different-account" } } } };
		yield return new object[] { b with { Operation = b.Operation with { Device = b.Operation.Device with { DeviceRegistrationId = "different-device" } } } };
		yield return new object[] { b with { Operation = b.Operation with { Device = b.Operation.Device with { LocalInstanceId = "different-instance" } } } };
		yield return new object[] { b with { AuthorKey = b.AuthorKey with { AuthorKeyVersion = "2" } } };
	}

	[Fact]
	public void Verify_RejectsAuthorKeyIdMismatch_ViaKidCheck()
	{
		(string compact, byte[] spki, MessageBinding binding) = BuildSigned();
		MessageBinding expected = binding with { AuthorKey = binding.AuthorKey with { AuthorKeyId = "different-authkey" } };
		var verifier = new MessagePackageVerifier();
		Assert.Throws<MessageBindingMismatchException>(() => verifier.Verify(compact, expected, spki));
	}

	[Fact]
	public void Verify_RejectsAuthorKeyAccountChatCrossFieldViolation()
	{
		MessageBinding bad = DefaultBinding() with { AuthorKey = new AuthorKeyReference("different-account", "chat-1", "authkey-1", "1") };
		(string compact, byte[] spki, _) = BuildSigned(bad);
		var verifier = new MessagePackageVerifier();
		Assert.Throws<MessageBindingMismatchException>(() => verifier.Verify(compact, bad, spki));
	}

	[Fact]
	public void Verify_RejectsWrongProtocolVersion()
	{
		MessageBinding bad = DefaultBinding() with { ProtocolVersion = 999 };
		(string compact, byte[] spki, _) = BuildSigned(bad);
		var verifier = new MessagePackageVerifier();
		Assert.Throws<MessageBindingMismatchException>(() => verifier.Verify(compact, bad, spki));
	}

	[Theory]
	[InlineData(11)]
	[InlineData(13)]
	public void Verify_RejectsWrongNonceLength(int nonceLength)
	{
		EncryptedContent badContent = DefaultContent() with { Nonce = Base64Url.Encode(new byte[nonceLength]) };
		(string compact, byte[] spki, MessageBinding binding) = BuildSigned(content: badContent);
		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsFormatException>(() => verifier.Verify(compact, binding, spki));
	}

	[Theory]
	[InlineData(15)]
	[InlineData(17)]
	public void Verify_RejectsWrongTagLength(int tagLength)
	{
		EncryptedContent badContent = DefaultContent() with { Tag = Base64Url.Encode(new byte[tagLength]) };
		(string compact, byte[] spki, MessageBinding binding) = BuildSigned(content: badContent);
		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsFormatException>(() => verifier.Verify(compact, binding, spki));
	}

	[Fact]
	public void Verify_RejectsInvalidAuthorPublicKey()
	{
		(string compact, _, MessageBinding binding) = BuildSigned();
		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsFormatException>(() => verifier.Verify(compact, binding, new byte[] { 1, 2, 3 }));
	}

	[Fact]
	public void Verify_RejectsUnknownPayloadField()
	{
		MessageBinding b = DefaultBinding();
		EncryptedContent c = DefaultContent();
		string payloadJson = $$"""{"binding":{{JsonSerializer.Serialize(b, SyncProtocol.CreateJsonOptions())}},"content":{{JsonSerializer.Serialize(c, SyncProtocol.CreateJsonOptions())}},"extra":"field"}""";
		byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

		using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		string compact = TestJwsBuilder.Build(ecdsa, b.AuthorKey.AuthorKeyId, payloadBytes);
		byte[] spki = ecdsa.ExportSubjectPublicKeyInfo();

		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsFormatException>(() => verifier.Verify(compact, b, spki));
	}

	[Fact]
	public void ComputeDigest_IsDeterministicForSameBytes()
	{
		(string compact, _, _) = BuildSigned();
		var verifier = new MessagePackageVerifier();
		Assert.Equal(verifier.ComputeDigest(compact), verifier.ComputeDigest(compact));
	}

	[Fact]
	public void ComputeDigest_DiffersForDifferentPackages()
	{
		(string compactA, _, _) = BuildSigned();
		(string compactB, _, _) = BuildSigned();
		var verifier = new MessagePackageVerifier();
		Assert.NotEqual(verifier.ComputeDigest(compactA), verifier.ComputeDigest(compactB));
	}

	// docs/sync-contract-v1.md: "Sequences, versions and counts are canonical decimal nonnegative
	// Int64 strings, max 9223372036854775807" - AuthorKeyVersion is a *version*, not exempted by the
	// AAD section's separate "restricted ASCII tokens" wording. Mirrors ChatAppClient/__tests__/
	// crypto/messagePackage.test.ts's "authorKeyVersion must be a canonical decimal Int64 string"
	// suite exactly: real signed packages, real crypto, not just unit-testing SyncProtocol.IsSequence
	// in isolation. Source written, reviewed, but NEVER COMPILED OR RUN under this session's
	// no-dotnet-build constraint.
	[Theory]
	[InlineData("v1")]
	[InlineData("01")]
	[InlineData("-1")]
	[InlineData("9223372036854775808")]
	[InlineData("")]
	[InlineData("1.0")]
	[InlineData(" 1")]
	public void Verify_RejectsNonCanonicalAuthorKeyVersion(string badVersion)
	{
		MessageBinding bad = DefaultBinding() with { AuthorKey = DefaultBinding().AuthorKey with { AuthorKeyVersion = badVersion } };
		(string compact, byte[] spki, _) = BuildSigned(bad);
		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsFormatException>(() => verifier.Verify(compact, bad, spki));
	}

	[Theory]
	[InlineData("0")]
	[InlineData("1")]
	[InlineData("9223372036854775807")]
	public void Verify_AcceptsCanonicalAuthorKeyVersion_RealSignedPackageRoundTrip(string goodVersion)
	{
		MessageBinding good = DefaultBinding() with { AuthorKey = DefaultBinding().AuthorKey with { AuthorKeyVersion = goodVersion } };
		(string compact, byte[] spki, _) = BuildSigned(good);
		var verifier = new MessagePackageVerifier();
		var verified = verifier.Verify(compact, good, spki);
		Assert.Equal(goodVersion, verified.Binding.AuthorKey.AuthorKeyVersion);
	}
}
