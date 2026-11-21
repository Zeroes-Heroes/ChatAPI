using Services.Sync.Contracts;

namespace Services.Crypto.Models;

/// <summary>
/// Result of <see cref="Services.Crypto.IMessagePackageVerifier"/>: the package's signature verified
/// against the caller-supplied author key, and its binding matches the caller's expected context
/// exactly. <see cref="Content"/> fields are still base64url text (decrypting them is out of scope
/// for the server - see docs/sync-contract-v1.md, "the server never sees plaintext").
/// </summary>
public sealed record VerifiedMessagePackage(
	MessageBinding Binding,
	EncryptedContent Content,
	byte[] NonceBytes,
	byte[] CiphertextBytes,
	byte[] TagBytes,
	string PackageDigest);
