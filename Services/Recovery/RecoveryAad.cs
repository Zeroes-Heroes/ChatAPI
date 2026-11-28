using System.Globalization;
using System.Text;
using System.Text.Json;
using Services.Crypto;
using Services.Recovery.Models;

namespace Services.Recovery;

/// <summary>
/// The exact recovery associated data docs/sync-contract-v1.md pins:
/// ["chat-recovery-v1","1",accountContextId,recoveryVersion,"argon2id","19",salt,memoryKiB,iterations,parallelism],
/// all strings, compact UTF-8 JSON, no spaces/BOM/trailing newline/extra fields. Mirrors
/// <c>ChatAppClient/src/Crypto/RecoveryEnvelope.ts</c>'s own builder byte-for-byte (same
/// <c>JsonSerializer.Serialize(string[])</c> / <c>JSON.stringify</c> discipline as
/// Services/Sessions/Service/DeviceProofVerifier.cs and Services/Keys/KeyProtectionAad.cs - every element
/// is a restricted-ASCII token, canonical base64url or canonical decimal, none of which escapes
/// differently between the two runtimes).
///
/// The server never holds the recovery key and therefore never decrypts with this AAD. It is still the
/// server's business: <see cref="Service.RecoveryEnvelopeService"/> rebuilds it at store time, which is
/// what proves the metadata it is about to persist can be re-emitted to a recovering device as the exact
/// same bytes the sealing device used. Metadata that cannot round-trip canonically is rejected rather
/// than stored as a silently unopenable envelope.
/// </summary>
public static class RecoveryAad
{
	private const string Domain = "chat-recovery-v1";
	private const string FormatMajorVersion = "1";

	public static byte[] Build(string accountContextId, long recoveryVersion, RecoveryKdfMetadata kdf)
	{
		string[] fields =
		[
			Domain,
			FormatMajorVersion,
			accountContextId,
			recoveryVersion.ToString(CultureInfo.InvariantCulture),
			kdf.Algorithm,
			kdf.Version.ToString(CultureInfo.InvariantCulture),
			Base64Url.Encode(kdf.Salt),
			kdf.MemoryKiB.ToString(CultureInfo.InvariantCulture),
			kdf.Iterations.ToString(CultureInfo.InvariantCulture),
			kdf.Parallelism.ToString(CultureInfo.InvariantCulture),
		];
		return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields));
	}
}
