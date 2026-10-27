namespace Services.Sync.Contracts;

public sealed record DeviceContext(
    string AccountContextId,
    string DeviceRegistrationId,
    string LocalInstanceId);

public sealed record OperationIdentity(
    DeviceContext Device,
    string ClientOperationId);

public sealed record MembershipScope(
    string ChatId,
    string MembershipId,
    string RightsVersion);

public sealed record SessionContext(
    DeviceContext Device,
    string SessionId,
    string TrustVersion);

