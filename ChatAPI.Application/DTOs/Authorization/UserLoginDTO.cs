namespace ChatAPI.Application.DTOs.Authorization
{
	public record UserLoginDTO(string Phone, string DeviceId, Guid SecretLoginCode);
}
