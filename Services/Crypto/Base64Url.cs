namespace Services.Crypto;

/// <summary>
/// Canonical unpadded base64url per docs/sync-contract-v1.md ("canonical unpadded base64url").
/// <see cref="TryDecode"/> rejects anything the canonical encoder would never produce (padding
/// characters, non-alphabet characters, wrong-length final group, non-zero padding bits) so a
/// signed package cannot smuggle a semantically-different byte string through a lenient decoder -
/// this is the single codec both <see cref="CompactJws"/> and the key-module AAD builder use.
/// </summary>
public static class Base64Url
{
	public static string Encode(ReadOnlySpan<byte> data)
	{
		string standard = Convert.ToBase64String(data);
		int padStart = standard.IndexOf('=');
		if (padStart >= 0)
		{
			standard = standard[..padStart];
		}
		return standard.Replace('+', '-').Replace('/', '_');
	}

	/// <summary>Strict decode: returns false (never throws) for any non-canonical input.</summary>
	public static bool TryDecode(string? value, out byte[] data)
	{
		data = [];
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}

		foreach (char c in value)
		{
			bool isAlphabet =
				(c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '-' or '_';
			if (!isAlphabet)
			{
				return false;
			}
		}

		int remainder = value.Length % 4;
		if (remainder == 1)
		{
			// No valid base64 grouping produces a final group of exactly 1 character.
			return false;
		}

		string standard = value.Replace('-', '+').Replace('_', '/');
		int padLength = remainder == 0 ? 0 : 4 - remainder;
		string padded = standard + new string('=', padLength);

		byte[] decoded;
		try
		{
			decoded = Convert.FromBase64String(padded);
		}
		catch (FormatException)
		{
			return false;
		}

		// Canonical round-trip check: a non-canonical encoding (e.g. non-zero padding bits in the
		// final group) decodes successfully but re-encodes to different text. Reject those so the
		// same bytes can never arrive under two different wire representations.
		if (Encode(decoded) != value)
		{
			return false;
		}

		data = decoded;
		return true;
	}
}
