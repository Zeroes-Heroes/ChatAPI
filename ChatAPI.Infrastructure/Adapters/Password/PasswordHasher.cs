using ChatAPI.Application.UseCases.Abstractions;
using BCryptNet = BCrypt.Net.BCrypt;

namespace ChatAPI.Infrastructure.Adapters.Password
{
	public class PasswordHasher : IPasswordHasher
	{
		public string HashPassword(string password) =>
			BCryptNet.HashPassword(password);

		public bool VerifyPassword(string password, string hashedPassword) =>
			BCryptNet.Verify(password, hashedPassword);
	}
}