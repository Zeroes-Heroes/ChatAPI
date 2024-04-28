namespace ChatAPI.Application.DTOs.User
{
	public class UserRegisterDTO
	{
		public required string Name { get; set; }
		public required string PhoneNumber { get; set; }
		public required string DeviceId { get; set; }
	}
}
