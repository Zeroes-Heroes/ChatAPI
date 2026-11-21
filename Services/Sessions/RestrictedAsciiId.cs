namespace Services.Sessions;

/// <summary>The one shared [A-Za-z0-9_-]{1,128} id-token rule (docs/sync-contract-v1.md: "IDs are case-sensitive ASCII tokens matching [A-Za-z0-9_-]{1,128}"), public for this module's own request-validation use. Mirrors <c>Services.Crypto.CompactJws.IsRestrictedAsciiId</c> exactly, but that one is internal to Services/Crypto and not accessible from Turbo - this is not a divergent rule, just a visible copy of the same one.</summary>
public static class RestrictedAsciiId
{
	public static bool IsValid(string? value) =>
		value is { Length: > 0 and <= 128 } && value.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');
}
