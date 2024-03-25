namespace Domain.Entities
{
	public class User(string email, string password)
	{
		public int Id { get; set; }
		public string Email { get; set; } = email;
		public string Password { get; set; } = password;
	}
}