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

	/// <summary>
	/// The binding the package CLAIMS, with no signature checked and nothing trusted. Its one legitimate
	/// use is deciding WHICH authorized state to verify against - the author key version a signature must
	/// be checked with can only be named by the package itself - after which the caller must build
	/// <paramref name="expectedBinding"/> for <see cref="Verify"/> from its own authorized state (session
	/// claims, the chat it already authorized, the registry row it already found) and let
	/// <see cref="Verify"/> be the thing that decides. Never treat anything returned here as a fact about
	/// the caller, the device, the chat or the author; the structural rules below are shape checks only.
	/// </summary>
	/// <exception cref="JwsFormatException">Malformed/non-canonical package shape or payload.</exception>
	MessageBinding ReadClaimedBinding(string signedPackage);

	/// <summary>base64url SHA-256 of the entire compact JWS ASCII bytes, per docs/sync-contract-v1.md PackageDigest.</summary>
	string ComputeDigest(string signedPackage);
}
