using Database.Entities;
using Services.Repositories.Base.Interface;
using Services.User.Models;

namespace Services.Repositories.User.Interface;

public interface IUserRepository : IBaseRepository
{
	void AddUserDevice(UserRegisterDTO dto);
	Task<UserEntity?> GetUserNoTracking(string phone);
	Task<UserEntity?> GetUserNoTracking(int userId);
	Task<UserEntity?> GetUserIncludingDevicesAndLoginCode(string phone);
	Task<bool> DoesUserExist(string phone);
	Task<UserEntity?> GetUserNoTracking(UserLoginDTO loginCredentials);
}
