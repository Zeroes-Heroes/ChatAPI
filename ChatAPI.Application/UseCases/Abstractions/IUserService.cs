using ChatAPI.Application.DTOs.User;
using ChatAPI.Application.Models.Authorization;
using ChatAPI.Application.Utilities;

namespace ChatAPI.Application.UseCases.Abstractions
{
	public interface IUserService
	{
		Task Register(UserRegisterDTO dto);
		Task<Result<TokensDTO>> Login(UserLoginDTO dto);
		Task Logout(int userId);
	}
}
