using System.Net;
using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Entities;

namespace ChatAPI.Application.UseCases.Implementations
{
    public class UserService(IUserRepository userRepo, ITokenService tokenService) : IUserService
	{
		public async Task<Result> Register(UserRegisterDTO dto)
		{
			userRepo.AddUser(dto);
			await userRepo.SaveChangesAsync();
			return Result.Success(HttpStatusCode.Created);
		}

		// TODO with Twilio: send sms code to phone number

		/// <summary>
		/// Verifies the sms code of the user, generates a secret login code and returns it.
		/// </summary>
		/// <returns>A secret login code</returns>
		public async Task<Result<SecretLoginCodeDTO>> VerifySmsCode(VerifySmsCodeDTO dto)
		{
			// TODO with Twilio: send request to verify phone + code

			UserDevice? userDevice = await userRepo.GetUserDeviceByDeviceIdAndPhoneWithUserIncluded(dto.Phone, dto.DeviceId);

			if (userDevice is null)
				return Result<SecretLoginCodeDTO>.Failure("Invalid phone and device id combination.");

			// The following is a custom implementation until we actually implement Twilio
			if (dto.SmsVerificationCode != "000000")
				return Result<SecretLoginCodeDTO>.Failure("Wrong sms verification code.");

			Guid secretLoginCode = Guid.NewGuid();

			userDevice.IsVerified = true;
			userDevice.User.UserLoginCode = new(userDevice.UserId, secretLoginCode);
			await userRepo.SaveChangesAsync();

			return Result<SecretLoginCodeDTO>.Success(new(secretLoginCode));
		}

		public async Task<Result<TokensDTO>> Login(UserLoginDTO userDto)
		{
			User? entity = await userRepo.GetUserByEmailAsNoTracking(userDto.Email);
			return Result<TokensDTO>.Success(await tokenService.GenerateTokens(entity!.Id));
		}

		public Task Logout(int userId) =>
			tokenService.RevokeTokens(userId);
	}
}