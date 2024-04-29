using System.Net;
using ChatAPI.Application.DTOs.User;
using ChatAPI.Application.Models.Authorization;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Entities;

namespace ChatAPI.Application.UseCases.Implementations
{
	public class UserService(IUserRepository userRepo, IPasswordHasher passwordManager, ITokenService tokenService) : IUserService
	{
		public async Task Register(UserRegisterDTO user)
		{
			await userRepo.AddUser(user);
			// send sms code to phone number
		}

		public async Task<Result<TokensDTO>> Login(UserLoginDTO userDto)
		{
			User? entity = await userRepo.GetUserByEmail(userDto.Email);
			return Result<TokensDTO>.Success(await tokenService.GenerateTokens(entity!.Id));

			//return Result<TokensDTO>.Failure("Invalid login credentials", (int)HttpStatusCode.Unauthorized);
		}

		public Task Logout(int userId) =>
			tokenService.RevokeTokens(userId);
	}
}