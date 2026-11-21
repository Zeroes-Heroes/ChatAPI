namespace Services.Sessions.Models;

/// <summary>Freshly minted session identity plus the raw (never-again-recoverable) opaque refresh-token secret, returned only once by <see cref="Interface.ISessionTokenIssuer"/>/<see cref="Interface.IRefreshTokenService"/> at the moment of issuance/rotation - every later lookup only ever has the hash.</summary>
public sealed record NewSession(string SessionId, long TrustVersion, string RawRefreshToken, DateTime RefreshTokenExpiresAtUtc);
