using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Domain.Entities;

namespace ChatAPI.Application.UseCases.Abstractions
{
	public interface IUserRepository
	{
		void AddUserDevice(UserRegisterDTO dto);
		Task<User?> GetUser(string phone);
		Task<User?> GetUserWithUserDevicesIncluded(string phone);
		Task<bool> DoesUserExist(string phone);
		Task<User?> GetUserForLoginNoTracking(UserLoginDTO loginCredentials);
		Task SaveChangesAsync();
	}
}
