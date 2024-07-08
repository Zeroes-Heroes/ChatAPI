namespace Services.User.Models;

public record UserLoginDTO(string Phone, string DeviceId, Guid SecretLoginCode);
