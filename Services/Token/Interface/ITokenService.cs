using Services.Token.Models;

namespace Services.Token.Interface;

public interface ITokenService
{
	Task<TokensDTO> GenerateTokens(int userId, string deviceId);
	Task RevokeTokens(int userId);
	Task<bool> ValidateAccessToken(string accessToken);
	Task<bool> ValidateRefreshToken(string refreshToken);
}