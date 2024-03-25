using Application.Models.Authorization;
using Application.UseCases.Abstractions;
using Domain.Entities;

namespace Application.UseCases.Implementations
{
	public class UserService(IUserRepository userRepo, IPasswordHasher passwordManager, ITokenService tokenService) : IUserService
	{
		public async Task<TokensDTO?> Login(string email, string providedPassword)
		{
			User? user = await userRepo.GetUserByEmail(email);

			if (UserCredentialsAreValid(providedPassword, user))
				return await tokenService.GenerateTokens(user.Id);

			return null;
		}

		public Task Logout(int userId) =>
			tokenService.RevokeTokens(userId);

		public async Task Register(string email, string password)
		{
			string hashedPassword = passwordManager.HashPassword(password);
			await userRepo.AddUser(email, hashedPassword);
		}

		private bool UserCredentialsAreValid(string providedPassword, User? user) =>
			user != null && passwordManager.VerifyPassword(providedPassword, user.Password);
	}
}