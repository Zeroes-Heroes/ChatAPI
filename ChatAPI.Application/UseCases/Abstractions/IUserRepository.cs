using ChatAPI.Application.DTOs.User;
using ChatAPI.Domain.Entities;

namespace ChatAPI.Application.UseCases.Abstractions
{
	public interface IUserRepository
	{	
		Task AddUser(UserRegisterDTO user);
		Task<User?> GetUserByUserId(int userId);
		Task<User?> GetUserByEmail(string email, bool trackEntity = true);
	}
}
