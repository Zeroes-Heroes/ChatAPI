using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Utilities.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Service;

/// <summary>Signs "app"-purpose access tokens with the same HMAC scheme/signing key as the legacy <see cref="Services.Token.Service.TokenService"/> bootstrap tokens (so both are validated by the one JwtBearer handler configured in ServiceCollectionExtension.AddAuthenticationConfigured), distinguished only by claims - see <see cref="SessionClaimTypes"/>.</summary>
public sealed class SessionTokenIssuer(IOptions<AppSettings> appSettings) : ISessionTokenIssuer
{
	private readonly AppSettings _appSettings = appSettings.Value;

	public WireContracts.SessionTokens IssueTokens(SessionIdentity identity, string rawRefreshToken, DateTime refreshTokenExpiresAtUtc, DateTime now)
	{
		DateTime accessTokenExpiresAtUtc = now.AddMinutes(ISessionTokenIssuer.AccessTokenLifetimeMinutes);

		SecurityTokenDescriptor descriptor = new()
		{
			Subject = new ClaimsIdentity(
			[
				new Claim(ClaimTypes.NameIdentifier, identity.LegacyUserId.ToString()),
				new Claim(SessionClaimTypes.TokenPurpose, SessionClaimTypes.AppPurpose),
				new Claim(SessionClaimTypes.AccountContextId, identity.AccountContextId),
				new Claim(SessionClaimTypes.SessionId, identity.SessionId),
				new Claim(SessionClaimTypes.DeviceRegistrationId, identity.DeviceRegistrationId),
				new Claim(SessionClaimTypes.LocalInstanceId, identity.LocalInstanceId),
				new Claim(SessionClaimTypes.TrustVersion, identity.TrustVersion.ToString()),
			]),
			Expires = accessTokenExpiresAtUtc,
			Issuer = _appSettings.TokenIssuer,
			Audience = _appSettings.TokenAudience,
			SigningCredentials = new SigningCredentials(
				new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_appSettings.TokenSigningKey)),
				SecurityAlgorithms.HmacSha512Signature),
		};

		string accessToken = new JsonWebTokenHandler().CreateToken(descriptor);

		WireContracts.SessionContext session = new(
			new WireContracts.DeviceContext(identity.AccountContextId, identity.DeviceRegistrationId, identity.LocalInstanceId),
			identity.SessionId,
			identity.TrustVersion.ToString());

		return new WireContracts.SessionTokens(
			session,
			accessToken,
			RfcTimestamp.Format(accessTokenExpiresAtUtc),
			rawRefreshToken,
			RfcTimestamp.Format(refreshTokenExpiresAtUtc));
	}
}
