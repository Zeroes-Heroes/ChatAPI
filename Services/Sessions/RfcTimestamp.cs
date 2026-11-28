using System.Globalization;

namespace Services.Sessions;

/// <summary>Formats a UTC <see cref="DateTime"/> as the wire contract's RFC3339 shape: "exactly three fractional digits and Z" (docs/sync-contract-v1.md). Server-authoritative values only - this module never parses a client-supplied timestamp string back into a <see cref="DateTime"/> for anything security-relevant.</summary>
public static class RfcTimestamp
{
	private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

	public static string Format(DateTime utc) =>
		utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
