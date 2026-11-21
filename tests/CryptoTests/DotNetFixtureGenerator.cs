using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Services.Crypto;
using Xunit;

namespace CryptoTests;

/// <summary>
/// REVERSE direction of <c>ChatAppClient/__tests__/crypto/fixtures.generate.test.ts</c>: generates
/// <c>Fixtures/dotnet-to-ts-fixture.json</c> - a real signed+encrypted SendText package built with
/// .NET's own platform crypto (<see cref="ECDsa"/>/<see cref="AesGcm"/> via
/// <see cref="TestJwsBuilder"/>'s exact JWS-building contract, the same one
/// <c>CompactJwsTests</c>/<c>MessagePackageVerifierTests</c> already use as "an external producer"),
/// for the TypeScript client crypto module to verify/decrypt with its own real production code
/// (<c>MessagePackage.verify</c>/<c>decryptContent</c>). This is the direction
/// review-report.md MEDIUM #9 found missing: the existing fixture only ever proves
/// TS-builds -&gt; .NET-verifies; this one is .NET-builds -&gt; TS-verifies, closing the
/// WebCrypto-vs-.NET half of "both directions" at the platform-crypto level (NOT at the native
/// Android/iOS level - QuickCryptoHost/ReactNativeKeychainStore remain a separate, still-BLOCKED gap;
/// see <c>ChatAppClient/__tests__/native/nativeCryptoHarness.ts</c> and the stage docs).
///
/// <b>THIS SOURCE FILE IS WRITTEN BUT HAS NEVER BEEN COMPILED OR RUN</b> in the session that added
/// it, per an explicit no-dotnet-build/no-dotnet-test-compilation constraint for that session. It
/// is reviewed-by-construction (every type/field/contract referenced here is cross-checked against
/// the actual production code it mirrors - <see cref="TestJwsBuilder"/>,
/// <c>Services.Sync.Contracts.MessageBinding</c>/<c>EncryptedContent</c>/<c>SignedMessagePayload</c>,
/// <c>SyncProtocol.CreateJsonOptions()</c>'s camelCase+strict-enum settings, and
/// <c>MessagePackageVerifier.ComputeDigest</c>'s own digest formula), not executed-and-passing. The
/// next session with build access must run it (<c>dotnet test --filter
/// DotNetFixtureGenerator</c>), inspect the written JSON for sanity, then manually copy
/// <c>Fixtures/dotnet-to-ts-fixture.json</c> to
/// <c>ChatAppClient/__tests__/crypto/fixtures/dotnet-to-ts-fixture.json</c> (same manual
/// cross-repo-worktree copy step <c>Fixtures/README.md</c> already documents for the other
/// direction) before the TS-side consumer test
/// (<c>__tests__/crypto/crossLanguageFixtureConsumer.test.ts</c>'s conditional "real .NET fixture"
/// case) will actually exercise real .NET-&gt;TS interop instead of staying skipped-as-pending.
/// </summary>
public class DotNetFixtureGenerator
{
	private const int MessageKeyLength = 32;
	private const int NonceLength = 12;
	private const int TagLength = 16;

	private sealed record DeviceContextJson(string AccountContextId, string DeviceRegistrationId, string LocalInstanceId);
	private sealed record OperationIdentityJson(DeviceContextJson Device, string ClientOperationId);
	private sealed record AuthorKeyReferenceJson(string AccountContextId, string ChatId, string AuthorKeyId, string AuthorKeyVersion);
	private sealed record MessageBindingJson(int ProtocolVersion, OperationIdentityJson Operation, string ChatId, AuthorKeyReferenceJson AuthorKey, string OperationKind);
	private sealed record EncryptedContentJson(string Nonce, string Ciphertext, string Tag);
	private sealed record SignedPayloadJson(MessageBindingJson Binding, EncryptedContentJson Content);
	private sealed record TamperedVariantsJson(string BitFlippedSignature, string SwappedBindingField, string MalformedHeaderAlg);

	private sealed record FixtureJson(
		string Note,
		string AuthorPublicKeySpkiBase64Url,
		MessageBindingJson ExpectedBinding,
		string Plaintext,
		string MessageKeyBase64Url,
		string SignedPackage,
		string PackageDigest,
		TamperedVariantsJson Tampered);

	/// <summary>
	/// NOT a correctness assertion over already-trusted production code (contrast
	/// <see cref="CrossLanguageFixtureTests"/>) - a generator, exactly like
	/// <c>fixtures.generate.test.ts</c> is on the TypeScript side. Written as an <c>[Fact]</c> only so
	/// it is runnable the same way (<c>dotnet test --filter ...</c>) once build access exists.
	/// </summary>
	[Fact]
	public void GenerateDotNetToTsFixture()
	{
		using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		byte[] spki = ecdsa.ExportSubjectPublicKeyInfo();

		var device = new DeviceContextJson("acct-1", "dev-1", "inst-1");
		var operation = new OperationIdentityJson(device, "op-1");
		var authorKey = new AuthorKeyReferenceJson("acct-1", "chat-1", "authkey-1", "1");
		const string chatId = "chat-1";
		const string plaintext = "dotnet cross-language fixture message";

		byte[] messageKey = RandomNumberGenerator.GetBytes(MessageKeyLength);
		byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
		byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
		byte[] ciphertext = new byte[plaintextBytes.Length];
		byte[] tag = new byte[TagLength];

		// Exact AAD contract from docs/sync-contract-v1.md / MessagePackage.ts's buildAad /
		// MessagePackageVerifier's own CryptoTests usage (CrossLanguageFixtureTests.cs):
		// ["chat-message-v1","1",accountContextId,deviceRegistrationId,localInstanceId,
		//  clientOperationId,chatId,authorKeyId,authorKeyVersion,"SendText"].
		string[] aadFields =
		[
			"chat-message-v1", "1",
			device.AccountContextId, device.DeviceRegistrationId, device.LocalInstanceId,
			operation.ClientOperationId, chatId, authorKey.AuthorKeyId, authorKey.AuthorKeyVersion,
			"SendText",
		];
		byte[] aad = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(aadFields));

		using (var aesGcm = new AesGcm(messageKey, TagLength))
		{
			aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);
		}

		var binding = new MessageBindingJson(1, operation, chatId, authorKey, "SendText");
		var content = new EncryptedContentJson(Base64Url.Encode(nonce), Base64Url.Encode(ciphertext), Base64Url.Encode(tag));
		var payload = new SignedPayloadJson(binding, content);

		// camelCase property names on the wire, matching SyncProtocol.CreateJsonOptions() /
		// MessagePackage.ts's `JSON.stringify({binding, content})` field-name casing exactly (both
		// sides use camelCase field names; .NET's JsonSerializer here uses the same casing policy
		// production code uses for this exact payload shape).
		var payloadJsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, payloadJsonOptions);

		string signedPackage = TestJwsBuilder.Build(ecdsa, authorKey.AuthorKeyId, payloadBytes);
		string packageDigest = ComputeDigest(signedPackage);

		// Tampered variants - same three as fixtures.generate.test.ts, for a symmetric fixture shape
		// the one TS consumer function (`checkCrossLanguageFixture`) can read for either direction.
		string[] segments = signedPackage.Split('.');
		Assert.True(Base64Url.TryDecode(segments[2], out byte[] sigBytes));
		sigBytes[0] ^= 0xff;
		string tamperedSignature = $"{segments[0]}.{segments[1]}.{Base64Url.Encode(sigBytes)}";

		// Only binding.chatId changes (not authorKey.chatId) - mirrors fixtures.generate.test.ts's
		// own tamper step (`payloadJson.binding.chatId = 'different-chat'`) exactly. Re-serializing
		// the strongly-typed payload with one field swapped (rather than hand-editing JSON text) is
		// both simpler and guaranteed to produce the same field order/casing as the original.
		var tamperedBindingValue = binding with { ChatId = "different-chat" };
		var tamperedPayload = new SignedPayloadJson(tamperedBindingValue, content);
		byte[] tamperedPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(tamperedPayload, payloadJsonOptions);
		string tamperedBindingPayloadSeg = Base64Url.Encode(tamperedPayloadBytes);
		string tamperedBinding = $"{segments[0]}.{tamperedBindingPayloadSeg}.{segments[2]}";

		string badHeaderJson = """{"alg":"HS256","typ":"chat-message+jws","kid":"authkey-1"}""";
		string badHeaderSeg = Base64Url.Encode(Encoding.UTF8.GetBytes(badHeaderJson));
		string malformedHeader = $"{badHeaderSeg}.{segments[1]}.{segments[2]}";

		var fixture = new FixtureJson(
			Note:
				"Generated by ChatAPI tests/CryptoTests/DotNetFixtureGenerator.cs from .NET's own " +
				"ECDsa/AesGcm via TestJwsBuilder's exact JWS-building contract (reverse direction of " +
				"ChatAppClient's fixtures.generate.test.ts). Regenerate by rerunning " +
				"GenerateDotNetToTsFixture; copy this file to " +
				"ChatAppClient/__tests__/crypto/fixtures/dotnet-to-ts-fixture.json for the TS side.",
			AuthorPublicKeySpkiBase64Url: Base64Url.Encode(spki),
			ExpectedBinding: binding,
			Plaintext: plaintext,
			MessageKeyBase64Url: Base64Url.Encode(messageKey),
			SignedPackage: signedPackage,
			PackageDigest: packageDigest,
			Tampered: new TamperedVariantsJson(tamperedSignature, tamperedBinding, malformedHeader));

		var writeOptions = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true,
		};
		string json = JsonSerializer.Serialize(fixture, writeOptions) + "\n";

		// AppContext.BaseDirectory is the test run's bin output dir
		// (tests/CryptoTests/bin/<Config>/<TFM>/); three levels up is the project source root -
		// same convention as every other "write back into the committed Fixtures/ source folder"
		// generator in a .NET test project. Writing anywhere under bin/ instead would make the output
		// invisible to source control and to the manual cross-repo copy step.
		string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
		string fixturesDir = Path.Combine(projectRoot, "Fixtures");
		Directory.CreateDirectory(fixturesDir);
		File.WriteAllText(Path.Combine(fixturesDir, "dotnet-to-ts-fixture.json"), json);

		// Prove the fixture is internally consistent before anything ever reaches TypeScript - same
		// discipline fixtures.generate.test.ts applies to its own output.
		using (var aesGcm = new AesGcm(messageKey, TagLength))
		{
			byte[] roundTripPlaintext = new byte[ciphertext.Length];
			aesGcm.Decrypt(nonce, ciphertext, tag, roundTripPlaintext, aad);
			Assert.Equal(plaintext, Encoding.UTF8.GetString(roundTripPlaintext));
		}
		using (ECDsa verifier = ECDsa.Create())
		{
			verifier.ImportSubjectPublicKeyInfo(spki, out _);
			byte[] signingInputAscii = Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}");
			Assert.True(Base64Url.TryDecode(segments[2], out byte[] signatureBytes));
			Assert.True(verifier.VerifyData(signingInputAscii, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
		}
	}

	/// <summary>Mirrors MessagePackageVerifier.ComputeDigest exactly: SHA-256 of the compact JWS's ASCII bytes, base64url-encoded.</summary>
	private static string ComputeDigest(string signedPackage)
	{
		byte[] asciiBytes = Encoding.ASCII.GetBytes(signedPackage);
		byte[] hash = SHA256.HashData(asciiBytes);
		return Base64Url.Encode(hash);
	}
}
