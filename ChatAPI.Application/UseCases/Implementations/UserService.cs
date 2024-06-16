using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Application.UseCases.Abstractions.Repositories;
using ChatAPI.Application.UseCases.Abstractions.Services;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Entities;
using System.Net;

namespace ChatAPI.Application.UseCases.Implementations
{
	public class UserService(IUserRepository userRepo, ITokenService tokenService) : IUserService
	{
		public async Task<Result> Register(UserRegisterDTO dto)
		{
			if (await userRepo.DoesUserExist(dto.Phone))
				return Result.Failure("This number is already in use.");

			userRepo.AddUserDevice(dto);
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
			User? user = await userRepo.GetUserIncludingDevicesAndLoginCode(dto.Phone);

			if (user is null)
				return Result<SecretLoginCodeDTO>.Failure("User with that phone is not found.", HttpStatusCode.NotFound);

			// The following is a custom implementation until we actually have Twilio
			if (dto.SmsVerificationCode != "000000")
				return Result<SecretLoginCodeDTO>.Failure("Wrong sms verification code.");

			user.UserDevices ??= [];

			UserDevice? userDevice = user.UserDevices.FirstOrDefault(ud => ud.DeviceId == dto.DeviceId);

			// Below code is commented because if you delete the app and install it again
			// and try to login, you will not be able to because the server will return the error.

			//if (userDevice is not null && userDevice.IsVerified)
			//	return Result<SecretLoginCodeDTO>.Failure("Device is already verified", HttpStatusCode.Forbidden);

			Guid secretLoginCode = default;

			if (userDevice is null)
			{
				userDevice = new UserDevice(dto.DeviceId);
				user.UserDevices.Add(userDevice);
				UserDevice? previousDevice = user.UserDevices.FirstOrDefault(ud => ud.IsVerified);

				if (previousDevice == null)
					secretLoginCode = Guid.NewGuid();
				else
					secretLoginCode = previousDevice.UserLoginCode!.SecretLoginCode;

				await userRepo.SaveChangesAsync();
				userDevice.UserLoginCode = new(userDevice.Id, secretLoginCode);
				userDevice.IsVerified = true;
				await userRepo.SaveChangesAsync();

				return Result<SecretLoginCodeDTO>.Success(new(secretLoginCode));
			}

			if (userDevice.IsVerified)
				secretLoginCode = userDevice.UserLoginCode!.SecretLoginCode;

			if (!userDevice.IsVerified)
			{
				secretLoginCode = Guid.NewGuid();
				userDevice.UserLoginCode = new(userDevice.Id, secretLoginCode);
				userDevice.IsVerified = true;
			}

			await userRepo.SaveChangesAsync();
			return Result<SecretLoginCodeDTO>.Success(new(secretLoginCode));
		}

		/// <summary>
		/// Used to verify the users credentials. 
		/// Generates access and refresh tokens.
		/// </summary>
		/// <param name="dto">The login credentials.</param>
		/// <returns>Access, refresh tokens and the name of the user.</returns>
		public async Task<Result<UserLoginResponseDTO>> Login(UserLoginDTO dto)
		{
			User? user = await userRepo.GetUserNoTracking(dto);

			if (user is null)
				return Result<UserLoginResponseDTO>.Failure("The login credentials are invalid.", HttpStatusCode.Unauthorized);

			return Result<UserLoginResponseDTO>.Success(new(user.Name, await tokenService.GenerateTokens(user!.Id)));
		}
	}
}