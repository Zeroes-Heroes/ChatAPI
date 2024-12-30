using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Services.Token.Enums;
using Services.Token.Interface;
using Services.Token.Models;
using Services.Utilities.Models;
using Services.Utilities.Statics;
using System.Security.Claims;
using System.Text;
using Services.Extensions;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

namespace Services.Token.Service;

public class TokenService(IOptions<AppSettings> appSettings, IDistributedCache cache) : ITokenService
{
	private const int AccessTokenTTL = 45;
	private const int RefreshTokenTTL = 60;

	private readonly AppSettings appSettings = appSettings.Value;

	/// <summary>
	/// <para>1. Revoke any old tokens the user might have.</para>
	/// <para>2. Generate new tokens.</para>
	/// <para>3. Cache the new tokens and returns them.</para>
	/// </summary>
	/// <param name="userId"></param>
	/// <returns></returns>
	public async Task<TokensDTO> GenerateTokens(int userId, string deviceId)
	{
		await RevokeTokens(userId);
		TokensDTO tokens = GenerateNewTokens(userId, deviceId, AccessTokenTTL, RefreshTokenTTL);
		await CacheTokens(cache, userId, tokens);

		return tokens;
	}

	/// <summary>
	/// Removes all user tokens from the cache.
	/// </summary>
	/// <param name="userId">The user id for which the tokens will be removed.</param>
	public async Task RevokeTokens(int userId)
	{
		await cache.RemoveAsync(string.Format(CacheKeys.AccessToken, userId));
		await cache.RemoveAsync(string.Format(CacheKeys.RefreshToken, userId));
	}

	public Task<bool> ValidateAccessToken(string accessToken) =>
		ValidateToken(accessToken, TokenType.AccessToken);

	public Task<bool> ValidateRefreshToken(string refreshToken) =>
		ValidateToken(refreshToken, TokenType.RefreshToken);

	public Task<string> GenerateApplePushNotificationToken()
	{
		string privateKeyText = File.ReadAllText(appSettings.ApplePrivateKeyPath, Encoding.UTF8);

		using var ecdsa = GetEcdsaParametersFromPrivateKey(privateKeyText);

		long iat = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

		var header = new JwtHeader
		{
			{ "alg", SecurityAlgorithms.EcdsaSha256 },
			{ "kid", appSettings.AppleKeyId},
		};

		var payload = new JwtPayload
		{
			{ "iat", iat },
			{ "iss", appSettings.AppleTeamId },
		};

		string headerJson = JsonSerializer.Serialize(header);
		string payloadJson = JsonSerializer.Serialize(payload);

		string encodeHeader = Base64UrlEncoder.Encode(headerJson);
		string encodePayload = Base64UrlEncoder.Encode(payloadJson);

		string usingToken = $"{encodeHeader}.{encodePayload}";

		var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(usingToken), HashAlgorithmName.SHA256);
		string encodedSignature = Base64UrlEncoder.Encode(signature);

		string tokenString = $"{usingToken}.{encodedSignature}";
		return Task.FromResult(tokenString);
	}

	private static async Task CacheTokens(IDistributedCache cache, int userId, TokensDTO tokens)
	{
		await CacheToken(cache, userId, tokens.AccessToken, CacheKeys.AccessToken, AccessTokenTTL);
		await CacheToken(cache, userId, tokens.RefreshToken, CacheKeys.RefreshToken, RefreshTokenTTL);
	}

	private async Task<bool> ValidateToken(string token, TokenType tokenType)
	{
		try
		{
			TokenValidationParameters tokenValidationParameters = GetTokenValidationParameters();
			TokenValidationResult principal = await new JsonWebTokenHandler().ValidateTokenAsync(token, tokenValidationParameters);

			string cacheKey = tokenType == TokenType.AccessToken
				? CacheKeys.AccessToken
				: CacheKeys.RefreshToken;

			string? tokenExistsInCache = await cache.GetStringAsync(
				string.Format(cacheKey, principal.Claims[ClaimTypes.NameIdentifier]));

			return !string.IsNullOrEmpty(tokenExistsInCache);
		}
		catch
		{
			return false;
		}
	}

	private TokenValidationParameters GetTokenValidationParameters() =>
		new()
		{
			ValidIssuer = appSettings.TokenIssuer,
			ValidAudience = appSettings.TokenAudience,
			IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(appSettings.TokenSigningKey)),
			ValidateIssuer = true,
			ValidateAudience = true,
			ValidateLifetime = true,
			ValidateIssuerSigningKey = true
		};

	/// <summary>
	/// General static method to cache a token.
	/// </summary>
	private TokensDTO GenerateNewTokens(int userId, string deviceId, int accessTokenTTL, int refreshTokenTTL)
	{
		DateTime now = DateTime.UtcNow;

		SecurityTokenDescriptor accessTokenDescriptor = GetTokenDescriptor(userId, deviceId, now.AddMinutes(accessTokenTTL));
		SecurityTokenDescriptor refreshTokenDescriptor = GetTokenDescriptor(userId, deviceId, now.AddMinutes(refreshTokenTTL));

		JsonWebTokenHandler handler = new();
		string accessToken = handler.CreateToken(accessTokenDescriptor);
		string refreshToken = handler.CreateToken(refreshTokenDescriptor);

		return new TokensDTO(accessToken, refreshToken);
	}

	private static Task CacheToken(IDistributedCache cache, int userId, string accessToken, string cacheKeyFormat, int expirationMinutes) =>
		cache.SetAsync(
			string.Format(cacheKeyFormat, userId),
			accessToken,
			DateTime.UtcNow.AddMinutes(expirationMinutes));

	private SecurityTokenDescriptor GetTokenDescriptor(int userId, string deviceId, DateTime expiresOn) =>
		new()
		{
			Subject = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(CustomClaimTypes.DeviceId, deviceId)]),
			Expires = expiresOn,
			Issuer = appSettings.TokenIssuer,
			Audience = appSettings.TokenAudience,
			SigningCredentials = GetSigningCredentials()
		};

	private SigningCredentials GetSigningCredentials() =>
		new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(appSettings.TokenSigningKey)),
			SecurityAlgorithms.HmacSha512Signature);

	private static ECDsa GetEcdsaParametersFromPrivateKey(string privateKeyContent)
	{
		var ecdsa = ECDsa.Create();
		ecdsa.ImportFromPem(privateKeyContent.ToCharArray());
		return ecdsa;
	}
}
