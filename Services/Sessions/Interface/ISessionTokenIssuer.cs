using Services.Sessions.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Interface;

/// <summary>Mints the "app"-purpose JWT access token plus the full wire <see cref="WireContracts.SessionTokens"/> bundle. The access token's claims are the only thing <see cref="ISessionTrustChecker"/> and the hub filter ever read to decide identity; this is the sole place those claims are written.</summary>
public interface ISessionTokenIssuer
{
	public const int AccessTokenLifetimeMinutes = 15;
	public const int RefreshTokenLifetimeDays = 30;

	/// <summary>Builds the complete bundle: a freshly signed access token for <paramref name="identity"/>, plus the already-generated <paramref name="rawRefreshToken"/>/<paramref name="refreshTokenExpiresAtUtc"/> the caller obtained from <see cref="IRefreshTokenService"/>.</summary>
	WireContracts.SessionTokens IssueTokens(SessionIdentity identity, string rawRefreshToken, DateTime refreshTokenExpiresAtUtc, DateTime now);
}
