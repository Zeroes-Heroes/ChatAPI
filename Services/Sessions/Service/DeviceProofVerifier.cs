using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Services.Crypto;
using Services.Sessions.Interface;
using Services.Sessions.Models;

namespace Services.Sessions.Service;

/// <summary>ES256-over-compact-JSON-array verification for device proof/approval, per docs/sync-contract-v1.md's "Sessions, enrollment and key protection" section. See <see cref="IDeviceProofVerifier"/>'s own doc comment for why this is a sibling of, not a reuse of, the message-package JWS verifier.</summary>
public sealed class DeviceProofVerifier : IDeviceProofVerifier
{
	private const string ProofDomain = "chat-device-proof-v1";
	private const string ApprovalDomain = "chat-device-approval-v1";
	private const string NistP256Oid = "1.2.840.10045.3.1.7";
	private const int SignatureLength = 64;

	public void VerifyDeviceProof(DeviceProofBinding binding, byte[] deviceSpki, byte[] signature) =>
		Verify(ProofDomain, binding, deviceSpki, signature);

	public void VerifyDeviceApproval(DeviceProofBinding binding, byte[] approvingDeviceSpki, byte[] signature) =>
		Verify(ApprovalDomain, binding, approvingDeviceSpki, signature);

	private static void Verify(string domain, DeviceProofBinding binding, byte[] spki, byte[] signature)
	{
		if (signature.Length != SignatureLength)
		{
			throw new DeviceProofInvalidException($"Signature must be exactly {SignatureLength} bytes (IEEE P1363 R||S), got {signature.Length}.");
		}

		byte[] signedBytes = BuildSignedBytes(domain, binding);

		using ECDsa ecdsa = ECDsa.Create();
		try
		{
			ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
		}
		catch (Exception ex) when (ex is CryptographicException or ArgumentException)
		{
			throw new DeviceProofInvalidException("Public key is not a valid DER SubjectPublicKeyInfo for P-256.");
		}

		// Compare the OID value, not FriendlyName - see Services/Crypto/Service/MessagePackageVerifier.cs's
		// own comment for why ("ECDSA_P256" vs "nistP256" are the same curve under different names).
		if (ecdsa.KeySize != 256 || !string.Equals(ecdsa.ExportParameters(false).Curve.Oid.Value, NistP256Oid, StringComparison.Ordinal))
		{
			throw new DeviceProofInvalidException("Public key is not a P-256 key.");
		}

		bool verified = ecdsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		if (!verified)
		{
			throw new DeviceProofInvalidException("ES256 signature verification failed.");
		}
	}

	/// <summary>
	/// Builds the exact UTF-8 compact JSON array docs/sync-contract-v1.md specifies:
	/// [domain,challengeId,purpose,accountContextId,deviceRegistrationId,keyId,spki,nonce,expiresAtUtc].
	/// <see cref="JsonSerializer.Serialize(string[])"/> is used the same way as
	/// Services/Keys/KeyProtectionAad.cs's AAD builder - every element here is a restricted-ASCII
	/// token, a base64url string or an RFC3339 timestamp, none of which JSON-escapes differently
	/// between this server-side build and the client's own `JSON.stringify` of the identical values.
	/// </summary>
	public static byte[] BuildSignedBytes(string domain, DeviceProofBinding binding)
	{
		string[] fields =
		[
			domain,
			binding.ChallengeId,
			binding.Purpose,
			binding.AccountContextId,
			binding.DeviceRegistrationId,
			binding.KeyId,
			Base64Url.Encode(binding.Spki),
			Base64Url.Encode(binding.Nonce),
			binding.ExpiresAtUtc,
		];
		return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields));
	}
}
