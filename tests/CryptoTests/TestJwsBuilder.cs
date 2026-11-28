using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Services.Crypto;

namespace CryptoTests;

/// <summary>
/// Builds compact JWS strings for test fixtures using .NET's own ECDsa directly (not through
/// `CompactJws`/`MessagePackageVerifier` - those are the production code under test). This plays the
/// role of "an external producer" the way the real TypeScript client module does in practice;
/// `CompactJwsTests`/`MessagePackageVerifierTests` verify the actual production verifier against
/// packages built here, plus the committed cross-language fixture from the real client module (see
/// `Fixtures/`).
/// </summary>
internal static class TestJwsBuilder
{
	public static (string Compact, ECDsa PublicKey) BuildWithFreshKey(string kid, byte[] payloadBytes)
	{
		using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		string compact = Build(ecdsa, kid, payloadBytes);
		ECDsa publicKey = ECDsa.Create();
		publicKey.ImportSubjectPublicKeyInfo(ecdsa.ExportSubjectPublicKeyInfo(), out _);
		return (compact, publicKey);
	}

	public static string Build(ECDsa privateKey, string kid, byte[] payloadBytes)
	{
		string headerJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["alg"] = "ES256", ["typ"] = "chat-message+jws", ["kid"] = kid });
		string headerSegment = Base64Url.Encode(Encoding.UTF8.GetBytes(headerJson));
		string payloadSegment = Base64Url.Encode(payloadBytes);
		string signingInput = $"{headerSegment}.{payloadSegment}";
		byte[] signature = privateKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		return $"{signingInput}.{Base64Url.Encode(signature)}";
	}

	public static byte[] RawPayload(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
}
