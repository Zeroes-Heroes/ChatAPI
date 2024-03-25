using System.Security.Claims;
using System.Text;
using Application.Models.Authorization;
using Application.UseCases.Abstractions;
using Infrastructure.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebAPI.Common;
using WebAPI.Services.Authorization.Enums;

namespace WebAPI.Services.Authorization
{
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
		public async Task<TokensDTO> GenerateTokens(int userId)
		{
			await RevokeTokens(userId);
			TokensDTO tokens = GenerateNewTokens(userId, AccessTokenTTL, RefreshTokenTTL);
			await CacheTokens(cache, userId, tokens);

			return tokens;
		}

		private static async Task CacheTokens(IDistributedCache cache, int userId, TokensDTO tokens)
		{
			await CacheToken(cache, userId, tokens.AccessToken, CacheKeys.AccessToken, AccessTokenTTL);
			await CacheToken(cache, userId, tokens.RefreshToken, CacheKeys.RefreshToken, RefreshTokenTTL);
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
		private TokensDTO GenerateNewTokens(int userId, int accessTokenTTL, int refreshTokenTTL)
		{
			DateTime now = DateTime.UtcNow;

			SecurityTokenDescriptor accessTokenDescriptor = GetTokenDescriptor(userId, now.AddMinutes(accessTokenTTL));
			SecurityTokenDescriptor refreshTokenDescriptor = GetTokenDescriptor(userId, now.AddMinutes(refreshTokenTTL));

			JsonWebTokenHandler handler = new JsonWebTokenHandler();
			string accessToken = handler.CreateToken(accessTokenDescriptor);
			string refreshToken = handler.CreateToken(refreshTokenDescriptor);

			return new TokensDTO(accessToken, refreshToken);
		}

		private static Task CacheToken(IDistributedCache cache, int userId, string accessToken, string cacheKeyFormat, int expirationMinutes) =>
			cache.SetAsync(
				string.Format(cacheKeyFormat, userId),
				accessToken,
				DateTime.UtcNow.AddMinutes(expirationMinutes));

		private SecurityTokenDescriptor GetTokenDescriptor(int userId, DateTime expiresOn) =>
			new()
			{
				Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.NameId, userId.ToString())]),
				Expires = expiresOn,
				Issuer = appSettings.TokenIssuer,
				Audience = appSettings.TokenAudience,
				SigningCredentials = GetSigningCredentials()
			};

		private SigningCredentials GetSigningCredentials() =>
			new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(appSettings.TokenSigningKey)),
				SecurityAlgorithms.HmacSha512Signature);
	}
}
