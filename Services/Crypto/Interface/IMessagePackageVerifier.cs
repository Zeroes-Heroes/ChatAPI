using Services.Crypto.Models;
using Services.Sync.Contracts;

namespace Services.Crypto.Interface;

/// <summary>
/// Verifies a client-produced SendText signed package per docs/sync-contract-v1.md: compact JWS
/// shape, ES256/P-256/SHA-256 signature, and that the package's own binding equals the caller's
/// expected context - never the package's self-declared claims. <paramref name="expectedBinding"/>
/// and <paramref name="authorPublicKeySpki"/> must come from the caller's own authorized state
/// (the current operation identity and the server-authorized author-key registry), not from the
/// package under verification; this interface has no way to look either of those up itself, by
/// design, so there is no path from "a package arrived" to "it gets treated as authentic" without
/// the caller first deciding who it ought to be from.
/// </summary>
public interface IMessagePackageVerifier
{
	/// <exception cref="JwsFormatException">Malformed/non-canonical package shape.</exception>
	/// <exception cref="JwsSignatureException">Signature does not verify against <paramref name="authorPublicKeySpki"/>.</exception>
	/// <exception cref="MessageBindingMismatchException">Verified binding does not equal <paramref name="expectedBinding"/>, or violates the payload's own cross-field rules.</exception>
	VerifiedMessagePackage Verify(string signedPackage, MessageBinding expectedBinding, byte[] authorPublicKeySpki);

	/// <summary>base64url SHA-256 of the entire compact JWS ASCII bytes, per docs/sync-contract-v1.md PackageDigest.</summary>
	string ComputeDigest(string signedPackage);
}
