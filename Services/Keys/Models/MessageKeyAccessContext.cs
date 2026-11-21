namespace Services.Keys.Models;

/// <summary>
/// What a caller must already know - from its own authorized state, not from the stored row - to
/// unwrap or rewrap a message's protected key. See docs/sync-contract-v1.md: "Verification must
/// bind caller's expected author/chat/message context, not self-declared package claims." There is
/// deliberately no "give me the key for this id" overload that skips supplying these.
/// </summary>
public sealed record MessageKeyAccessContext(string MessageId, string ChatId);
