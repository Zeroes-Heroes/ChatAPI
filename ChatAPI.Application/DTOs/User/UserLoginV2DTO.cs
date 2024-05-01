namespace ChatAPI.Application.DTOs.User
{
    public record UserLoginV2DTO(string Phone, string DeviceId, string SecretLoginCode);
}
