using ChatAPI.Application.DTOs.User;
using ChatAPI.Application.Models.Authorization;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Entities;
using System.Net;

namespace ChatAPI.Application.UseCases.Implementations
{
    public class UserService(IUserRepository userRepo, ITokenService tokenService) : IUserService
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

        /// <summary>
        /// Used to verify the users credentials. 
        /// Generates access and refresh tokens.
        /// The second version of the login method.
        /// </summary>
        /// <param name="userDto">The login credentials.</param>
        /// <returns>Access and refresh tokens.</returns>
        public async Task<Result<TokensDTO>> LoginV2(UserLoginV2DTO userDto)
        {
            User? entity = await userRepo.GetUserForLogin(userDto, false);

            if (entity == null)
                return Result<TokensDTO>.Failure("The login credentials are invalid.", (int)HttpStatusCode.Unauthorized);

            return Result<TokensDTO>.Success(await tokenService.GenerateTokens(entity!.Id));
        }

        public Task Logout(int userId) =>
            tokenService.RevokeTokens(userId);
    }
}