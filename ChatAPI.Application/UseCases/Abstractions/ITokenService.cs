using ChatAPI.Application.Models.Authorization;

namespace ChatAPI.Application.UseCases.Abstractions
{
	public interface ITokenService
	{
		Task<TokensDTO> GenerateTokens(int userId);
		Task RevokeTokens(int userId);
		Task<bool> ValidateAccessToken(string accessToken);
		Task<bool> ValidateRefreshToken(string refreshToken);
	}
}