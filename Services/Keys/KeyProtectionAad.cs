using System.Text;
using System.Text.Json;
using Services.Keys.Models;

namespace Services.Keys;

/// <summary>
/// AAD for the server protected-key storage format, in the same style as docs/sync-contract-v1.md's
/// client-side AADs (a versioned, all-string, compact JSON array): ["chat-server-key-v1","1",
/// formatVersion,masterKeyVersion,purpose,ownerContextId,keyId]. Binding the master key version in
/// means a stored row can only ever be unprotected under the exact version it says it used - a
/// rewrap must re-encrypt (new nonce/ciphertext/tag/AAD), never just relabel <see cref="ProtectedKeyMaterial.MasterKeyVersion"/>
/// on old bytes.
/// </summary>
internal static class KeyProtectionAad
{
	private const string Domain = "chat-server-key-v1";
	private const string FormatMajorVersion = "1";

	public static byte[] Build(string masterKeyVersion, KeyProtectionContext context)
	{
		string[] fields =
		[
			Domain,
			FormatMajorVersion,
			context.FormatVersion,
			masterKeyVersion,
			context.Purpose,
			context.OwnerContextId,
			context.KeyId,
		];
		return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields));
	}
}
