using Domain.Entities;

namespace Application.UseCases.Abstractions
{
	public interface IUserRepository
	{
		/// <summary>
		/// Gets the user entity by email
		/// </summary>
		/// <param name="email">The email of the user</param>
		Task<User?> GetUserByEmail(string email);
		Task AddUser(string email, string hashedPassword);
	}
}
