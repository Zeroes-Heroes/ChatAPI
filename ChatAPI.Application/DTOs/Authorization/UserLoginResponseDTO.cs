namespace ChatAPI.Application.DTOs.Authorization
{
	public record UserLoginResponseDTO(string? Name, TokensDTO? Tokens);
}
