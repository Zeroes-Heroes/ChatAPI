namespace Services.Sync.Contracts;

public sealed record AuthorKeyReference(
    string AccountContextId,
    string ChatId,
    string AuthorKeyId,
    string AuthorKeyVersion);

public sealed record AuthorVerificationKey(
    AuthorKeyReference Reference,
    string Spki);

public sealed record MessageBinding(
    int ProtocolVersion,
    OperationIdentity Operation,
    string ChatId,
    AuthorKeyReference AuthorKey,
    OperationKind OperationKind);

public sealed record EncryptedContent(
    string Nonce,
    string Ciphertext,
    string Tag);

public sealed record SignedMessagePayload(
    MessageBinding Binding,
    EncryptedContent Content);

/// <summary>
/// Stage 11: publishes the public half of one author context to the server-authorized author-key
/// registry. The account in <paramref name="Reference"/> must be the caller's own authenticated one;
/// the server binds the reference to <paramref name="Spki"/> once and refuses any later, different key
/// for the same reference. Separate from <see cref="SendMessageRequest"/> on purpose - a package may
/// never carry the key it is verified against.
/// </summary>
/// <param name="Spki">Canonical unpadded base64url of the DER SubjectPublicKeyInfo for P-256.</param>
public sealed record AuthorKeyRegistrationRequest(
    int ProtocolVersion,
    AuthorKeyReference Reference,
    string Spki);

/// <summary><paramref name="AlreadyRegistered"/> makes a retried registration a confirmation rather than a second write.</summary>
public sealed record AuthorKeyRegistrationResult(
    AuthorKeyReference Reference,
    bool AlreadyRegistered);

public sealed record SendMessageRequest(
    int ProtocolVersion,
    MembershipScope Scope,
    string SignedPackage,
    string MessageKey);

public sealed record OperationQuery(
    int ProtocolVersion,
    OperationIdentity Operation);

public sealed record AcceptedMessage(
    OperationIdentity Operation,
    string PackageDigest,
    string MessageId,
    string ChatId,
    string MessageSequence,
    string ChangeSequence,
    string CreatedAtUtc,
    string OwnSendThroughSequence);

public sealed record MessageEnvelope(
    string MessageId,
    string ChatId,
    string SenderAccountContextId,
    string MessageSequence,
    string Version,
    string CreatedAtUtc,
    string SignedPackage);

public sealed record MessageKeyGrant(
    string MessageId,
    string KeyVersion,
    string MessageKey);

public sealed record MessageDelivery(
    MessageEnvelope Message,
    MessageKeyGrant Key,
    AuthorVerificationKey AuthorVerificationKey,
    MembershipScope Scope);

