namespace Services.Crypto;

/// <summary>Thrown for any structural/encoding defect in a compact JWS string, before signature verification is even attempted.</summary>
public sealed class JwsFormatException(string message) : Exception(message);

/// <summary>Thrown when the ES256 signature itself does not verify against the supplied public key.</summary>
public sealed class JwsSignatureException(string message) : Exception(message);
