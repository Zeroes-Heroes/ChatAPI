using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Application.UseCases.Abstractions;
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
			User? user = await userRepo.GetUserWithUserDevicesIncluded(dto.Phone);

			if (user is null)
				return Result<SecretLoginCodeDTO>.Failure("User with that phone is not found.", HttpStatusCode.NotFound);

			// The following is a custom implementation until we actually have Twilio
			if (dto.SmsVerificationCode != "000000")
				return Result<SecretLoginCodeDTO>.Failure("Wrong sms verification code.");

			user.UserDevices ??= new List<UserDevice>();

			UserDevice? userDevice = user.UserDevices.FirstOrDefault(ud => ud.DeviceId == dto.DeviceId);

			if (userDevice is not null && userDevice.IsVerified)
				return Result<SecretLoginCodeDTO>.Failure("Device is already verified", HttpStatusCode.Forbidden);

			if (userDevice is null)
			{
				userDevice = new UserDevice(dto.DeviceId);
				user.UserDevices.Add(userDevice);
			}

			Guid secretLoginCode = Guid.NewGuid();

			userDevice.IsVerified = true;
			userDevice.UserLoginCode = new(user.Id, secretLoginCode);
			await userRepo.SaveChangesAsync();

			return Result<SecretLoginCodeDTO>.Success(new(secretLoginCode));
		}

		/// <summary>
		/// Used to verify the users credentials. 
		/// Generates access and refresh tokens.
		/// The second version of the login method.
		/// </summary>
		/// <param name="userDto">The login credentials.</param>
		/// <returns>Access and refresh tokens.</returns>
		public async Task<Result<UserLoginResponseDTO>> Login(UserLoginDTO userDto)
		{
			User? entity = await userRepo.GetUserForLoginNoTracking(userDto);

			if (entity == null)
				return Result<UserLoginResponseDTO>.Failure("The login credentials are invalid.", HttpStatusCode.Unauthorized);

			return Result<UserLoginResponseDTO>.Success(new(entity.Name, await tokenService.GenerateTokens(entity!.Id)));
		}
	}
}