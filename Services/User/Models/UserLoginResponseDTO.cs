using Services.Token.Models;

namespace Services.User.Models;

public record UserLoginResponseDTO(string? Name, TokensDTO? Tokens, int UserId);
