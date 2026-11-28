namespace Services.Crypto;

/// <summary>
/// Thrown when a signed package's own (already signature-verified) binding does not equal the
/// caller's expected context. The caller's expected values come from its own authorized session/
/// operation state, never from the package itself - see docs/sync-contract-v1.md: "The key comes
/// from the server-authorized author-key registry ..., never from an untrusted JWS header or
/// embedded client-supplied key." This also covers the payload's own internal cross-field rule
/// ("AuthorKey account/chat must equal the operation account/binding chat").
/// </summary>
public sealed class MessageBindingMismatchException(string message) : Exception(message);
