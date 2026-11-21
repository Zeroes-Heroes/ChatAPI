using Services.Sessions.Models;

namespace Services.Sessions.Interface;

/// <summary>
/// Verifies the raw ECDSA signatures docs/sync-contract-v1.md's "Sessions, enrollment and key
/// protection" section defines - structurally simpler than <see cref="Services.Crypto.Interface.IMessagePackageVerifier"/>'s
/// JWS (no header/compact-segment framing, just ES256 over one UTF-8 compact JSON array), so this is
/// a sibling primitive, not a reuse of that message-package-shaped verifier. Pure cryptography: takes
/// its expected <see cref="DeviceProofBinding"/> and the caller's already-authorized public key (the
/// device's own registered key for a device proof, or an approving device's currently-trusted
/// registered key for an approval) - never a key embedded in the request itself.
/// </summary>
public interface IDeviceProofVerifier
{
	/// <summary>Verifies a new device's own chat-device-proof-v1 signature over <paramref name="binding"/>, using <paramref name="deviceSpki"/> (DER SubjectPublicKeyInfo, P-256).</summary>
	/// <exception cref="DeviceProofInvalidException">The signature does not verify, is the wrong length, or <paramref name="deviceSpki"/> is not a valid P-256 key.</exception>
	void VerifyDeviceProof(DeviceProofBinding binding, byte[] deviceSpki, byte[] signature);

	/// <summary>Verifies an approving trusted device's chat-device-approval-v1 signature over the SAME binding fields as <see cref="VerifyDeviceProof"/> (domain differs; see docs/sync-contract-v1.md), using <paramref name="approvingDeviceSpki"/>.</summary>
	/// <exception cref="DeviceProofInvalidException">The signature does not verify, is the wrong length, or <paramref name="approvingDeviceSpki"/> is not a valid P-256 key.</exception>
	void VerifyDeviceApproval(DeviceProofBinding binding, byte[] approvingDeviceSpki, byte[] signature);
}
