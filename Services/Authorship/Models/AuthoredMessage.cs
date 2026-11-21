namespace Services.Authorship.Models;

/// <summary>
/// The three facts an author right is decided from: which message, who wrote it, and when (the instant
/// a permitted window is evaluated at). Deliberately the legacy shape - real messages still live in the
/// legacy <c>Messages</c> table until stage 11.
/// </summary>
public readonly record struct AuthoredMessage(int MessageId, int SenderLegacyUserId, DateTime CreatedAtUtc);
