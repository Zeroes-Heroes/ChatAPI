using Application.Models.Authorization;

namespace Application.UseCases.Abstractions
{
	public interface ITokenService
	{
		Task<TokensDTO> GenerateTokens(int userId);
		Task RevokeTokens(int userId);
		Task<bool> ValidateAccessToken(string accessToken);
		Task<bool> ValidateRefreshToken(string refreshToken);
	}
}