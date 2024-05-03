namespace ChatAPI.Application.DTOs.Authorization
{
    public record UserLoginReturnDTO(string? UserName, TokensDTO? Tokens);
}
