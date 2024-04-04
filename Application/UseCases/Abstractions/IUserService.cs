using Application.DTOs.User;
using Application.Models.Authorization;
using Application.Utilities;

namespace Application.UseCases.Abstractions
{
	public interface IUserService
	{
		Task Register(UserRegisterDTO dto);
		Task<Result<TokensDTO>> Login(UserLoginDTO dto);
		Task Logout(int userId);
	}
}
