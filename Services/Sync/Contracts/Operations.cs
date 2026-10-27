using System.Text.Json.Serialization;

namespace Services.Sync.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(AcceptedOperation), "Accepted")]
[JsonDerivedType(typeof(UnknownOperation), "Unknown")]
[JsonDerivedType(typeof(RetryableOperation), "Retryable")]
[JsonDerivedType(typeof(RejectedOperation), "Rejected")]
public abstract record OperationResult(OperationIdentity Operation);

public sealed record AcceptedOperation(OperationIdentity Operation, AcceptedMessage Result)
    : OperationResult(Operation);

public sealed record UnknownOperation(OperationIdentity Operation) : OperationResult(Operation);

public sealed record RetryableOperation(
    OperationIdentity Operation, OperationErrorCode Code, int RetryAfterMs)
    : OperationResult(Operation);

public sealed record RejectedOperation(OperationIdentity Operation, OperationErrorCode Code)
    : OperationResult(Operation);
