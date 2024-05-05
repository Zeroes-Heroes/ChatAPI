using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Domain.Entities;

namespace ChatAPI.Application.UseCases.Abstractions
{
	public interface IUserRepository
	{
		void AddUserDevice(UserRegisterDTO dto);
		Task<User?> GetUser(string phone);
		Task<User?> GetUser_DevicesAndLoginCodeIncluded(string phone);
		Task<bool> DoesUserExist(string phone);
		Task<User?> GetUserNoTracking(UserLoginDTO loginCredentials);
		Task SaveChangesAsync();
	}
}
