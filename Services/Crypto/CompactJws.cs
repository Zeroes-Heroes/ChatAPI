using System.Text;
using System.Text.Json;
using Services.Crypto.Models;
using Services.Sync.Contracts;

namespace Services.Crypto;

/// <summary>
/// Structural parsing of a compact JWS per docs/sync-contract-v1.md: exactly 3 non-empty dot-separated
/// canonical-base64url segments, a protected header with exactly {alg:"ES256", typ:"chat-message+jws",
/// kid:&lt;string&gt;} and nothing else, and a 64-byte (IEEE P1363 R||S) signature. This class only
/// validates shape - it never touches cryptography (see <see cref="MessagePackageVerifier"/> for the
/// ES256 signature check) and never deserializes the payload (the caller does that once it has
/// verified the signature over these exact bytes).
/// </summary>
public static class CompactJws
{
	public const string ExpectedAlg = "ES256";
	public const string ExpectedTyp = "chat-message+jws";
	private const int SignatureLength = 64;

	public static ParsedJws Parse(string compact)
	{
		if (string.IsNullOrEmpty(compact))
		{
			throw new JwsFormatException("Signed package must be a non-empty string.");
		}

		// Cheap, early guard before any decode/parse work - the whole compact JWS is restricted
		// ASCII (base64url segments + '.' separators), so string Length is an exact byte-length
		// proxy here. docs/sync-contract-v1.md's MaxSignedPackageBytes is a transport-layer limit;
		// this public parser enforces it directly too, rather than requiring every future caller
		// to remember to check first (mirrors ChatAppClient/src/Crypto/Jws.ts's `parse`).
		if (compact.Length > SyncProtocol.MaxSignedPackageBytes)
		{
			throw new JwsFormatException($"Signed package exceeds the maximum size of {SyncProtocol.MaxSignedPackageBytes} bytes.");
		}

		string[] segments = compact.Split('.');
		if (segments.Length != 3)
		{
			throw new JwsFormatException($"Compact JWS must have exactly 3 segments, got {segments.Length}.");
		}

		// Empty segments 1 is a detached payload; any empty segment indicates a malformed/truncated package.
		if (segments[0].Length == 0 || segments[1].Length == 0 || segments[2].Length == 0)
		{
			throw new JwsFormatException("Compact JWS segments must all be non-empty (detached payloads are not allowed).");
		}

		if (!Base64Url.TryDecode(segments[0], out byte[] headerBytes))
		{
			throw new JwsFormatException("Protected header is not canonical unpadded base64url.");
		}

		if (!Base64Url.TryDecode(segments[1], out byte[] payloadBytes))
		{
			throw new JwsFormatException("Payload is not canonical unpadded base64url.");
		}

		if (!Base64Url.TryDecode(segments[2], out byte[] signatureBytes))
		{
			throw new JwsFormatException("Signature is not canonical unpadded base64url.");
		}

		if (signatureBytes.Length != SignatureLength)
		{
			throw new JwsFormatException($"ES256 signature must be exactly {SignatureLength} bytes (IEEE P1363 R||S), got {signatureBytes.Length}.");
		}

		string kid = ParseAndValidateHeader(headerBytes);
		string signingInput = segments[0] + "." + segments[1];

		return new ParsedJws(headerBytes, payloadBytes, signatureBytes, signingInput, kid);
	}

	private static string ParseAndValidateHeader(byte[] headerBytes)
	{
		try
		{
			return ParseAndValidateHeaderCore(headerBytes);
		}
		catch (System.Text.Json.JsonException ex)
		{
			throw new JwsFormatException($"Malformed protected header: {ex.Message}");
		}
	}

	private static string ParseAndValidateHeaderCore(byte[] headerBytes)
	{
		string? alg = null;
		string? typ = null;
		string? kid = null;
		var seenKeys = new HashSet<string>(StringComparer.Ordinal);

		var readerOptions = new JsonReaderOptions
		{
			CommentHandling = JsonCommentHandling.Disallow,
			AllowTrailingCommas = false,
		};
		var reader = new Utf8JsonReader(headerBytes, readerOptions);

		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
		{
			throw new JwsFormatException("Protected header must be a JSON object.");
		}

		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				throw new JwsFormatException("Malformed protected header.");
			}

			string key = reader.GetString() ?? throw new JwsFormatException("Malformed protected header property name.");
			if (!seenKeys.Add(key))
			{
				throw new JwsFormatException($"Duplicate protected header field '{key}'.");
			}

			if (!reader.Read())
			{
				throw new JwsFormatException("Malformed protected header.");
			}

			switch (key)
			{
				case "alg":
					alg = RequireString(reader, key);
					break;
				case "typ":
					typ = RequireString(reader, key);
					break;
				case "kid":
					kid = RequireString(reader, key);
					break;
				default:
					// Unknown fields (including b64/crit) are rejected, not ignored: the contract requires
					// "Disallow ... crit extensions. Reject duplicate or unknown header ... fields."
					throw new JwsFormatException($"Unknown protected header field '{key}'.");
			}
		}

		if (reader.TokenType != JsonTokenType.EndObject)
		{
			throw new JwsFormatException("Malformed protected header.");
		}

		// Reject trailing bytes after the single JSON object (e.g. a second top-level value).
		if (reader.Read())
		{
			throw new JwsFormatException("Protected header has trailing content after the JSON object.");
		}

		if (seenKeys.Count != 3 || alg is null || typ is null || kid is null)
		{
			throw new JwsFormatException("Protected header must contain exactly alg, typ and kid.");
		}

		if (!string.Equals(alg, ExpectedAlg, StringComparison.Ordinal))
		{
			throw new JwsFormatException($"Unsupported JWS alg '{alg}'; only {ExpectedAlg} is accepted.");
		}

		if (!string.Equals(typ, ExpectedTyp, StringComparison.Ordinal))
		{
			throw new JwsFormatException($"Unsupported JWS typ '{typ}'; only {ExpectedTyp} is accepted.");
		}

		if (kid.Length == 0 || kid.Length > 128 || !IsRestrictedAsciiId(kid))
		{
			throw new JwsFormatException("Protected header 'kid' must be a restricted ASCII id token.");
		}

		return kid;
	}

	private static string RequireString(Utf8JsonReader reader, string field)
	{
		if (reader.TokenType != JsonTokenType.String)
		{
			throw new JwsFormatException($"Protected header field '{field}' must be a string.");
		}
		return reader.GetString() ?? throw new JwsFormatException($"Protected header field '{field}' must be a string.");
	}

	/// <summary>IDs are case-sensitive ASCII tokens matching [A-Za-z0-9_-]{1,128} per docs/sync-contract-v1.md.</summary>
	internal static bool IsRestrictedAsciiId(string value)
	{
		if (value.Length is 0 or > 128)
		{
			return false;
		}
		foreach (char c in value)
		{
			bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '-' or '_';
			if (!ok)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>ASCII bytes of the exact signed input, for use with ECDsa.VerifyData.</summary>
	public static byte[] SigningInputBytes(string signingInputAscii) => Encoding.ASCII.GetBytes(signingInputAscii);
}
