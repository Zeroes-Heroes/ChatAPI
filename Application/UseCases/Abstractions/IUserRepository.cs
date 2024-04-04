using Application.DTOs.User;
using Domain.Entities;

namespace Application.UseCases.Abstractions
{
	public interface IUserRepository
	{	
		Task AddUser(UserRegisterDTO user);
		Task<User?> GetUserByUserId(int userId);
		Task<User?> GetUserByEmail(string email, bool trackEntity = true);
	}
}
