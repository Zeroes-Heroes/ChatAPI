using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Domain.Entities;

namespace ChatAPI.Application.UseCases.Abstractions.Repositories
{
    public interface IUserRepository : IBaseRepository
    {
        void AddUserDevice(UserRegisterDTO dto);
        Task<User?> GetUserNoTracking(string phone);
		Task<User?> GetUserNoTracking(int userId);
        Task<User?> GetUserIncludingDevicesAndLoginCode(string phone);
		Task<bool> DoesUserExist(string phone);
        Task<User?> GetUserNoTracking(UserLoginDTO loginCredentials);
    }
}
