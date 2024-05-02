using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Domain.Entities;

namespace ChatAPI.Application.UseCases.Abstractions
{
    public interface IUserRepository
	{
		void AddUser(UserRegisterDTO dto);
		Task<User?> GetUserByEmailAsNoTracking(string email);
		Task<UserDevice?> GetUserDeviceByDeviceIdAndPhoneWithUserIncluded(string phone, string deviceId);
		Task SaveChangesAsync();
	}
}
