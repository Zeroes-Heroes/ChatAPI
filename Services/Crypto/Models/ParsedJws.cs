namespace Services.Crypto.Models;

/// <summary>
/// The result of structurally parsing (not yet signature-verifying) a compact JWS string per
/// docs/sync-contract-v1.md. <see cref="SigningInputAscii"/> is the exact ASCII text that was
/// signed (base64url(header) + "." + base64url(payload)) - verification must sign/verify these
/// bytes directly, never re-derive them by re-serializing <see cref="PayloadBytes"/>.
/// </summary>
public sealed record ParsedJws(
	byte[] ProtectedHeaderBytes,
	byte[] PayloadBytes,
	byte[] SignatureBytes,
	string SigningInputAscii,
	string Kid);
