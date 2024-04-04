using System.Net;
using Application.DTOs.User;
using Application.Models.Authorization;
using Application.UseCases.Abstractions;
using Application.Utilities;
using Domain.Entities;

namespace Application.UseCases.Implementations
{
	public class UserService(IUserRepository userRepo, IPasswordHasher passwordManager, ITokenService tokenService) : IUserService
	{
		public async Task Register(UserRegisterDTO user)
		{
			user.Password = passwordManager.HashPassword(user.Password);
			await userRepo.AddUser(user);
		}

		public async Task<Result<TokensDTO>> Login(UserLoginDTO userDto)
		{
			User? entity = await userRepo.GetUserByEmail(userDto.Email);

			if (UserCredentialsAreValid(userDto.Password, entity))
				return Result<TokensDTO>.Success(await tokenService.GenerateTokens(entity!.Id));

			return Result<TokensDTO>.Failure("Invalid login credentials", (int)HttpStatusCode.Unauthorized);
		}

		public Task Logout(int userId) =>
			tokenService.RevokeTokens(userId);

		private bool UserCredentialsAreValid(string providedPassword, User? user) =>
			user != null && passwordManager.VerifyPassword(providedPassword, user.Password);
	}
}