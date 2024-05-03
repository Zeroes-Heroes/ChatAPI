namespace ChatAPI.Application.DTOs.Authorization
{
	public record UserLoginResponseDTO(string? UserName, TokensDTO? Tokens);
}
