namespace Application.DTOs.User
{
	public class UserRegisterDTO
	{
		public required string Email { get; set; }
		public required string Password { get; set; }
		public required string Phone { get; set; }
	}
}
