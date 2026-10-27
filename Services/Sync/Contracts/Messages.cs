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

