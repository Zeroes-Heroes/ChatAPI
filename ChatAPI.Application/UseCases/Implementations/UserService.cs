using ChatAPI.Application.Common;
using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Application.UseCases.Abstractions.Repositories;
using ChatAPI.Application.UseCases.Abstractions.Services;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Entities;
using Microsoft.Extensions.Options;
using System.Net;
using Twilio.Exceptions;
using Twilio.Rest.Verify.V2.Service;

namespace ChatAPI.Application.UseCases.Implementations
{
	public class UserService(IUserRepository userRepo, ITokenService tokenService, IOptions<AppSettings> options) : IUserService
	{
		private readonly AppSettings appSettings = options.Value;

		public async Task<Result> Register(UserRegisterDTO dto)
		{
			if (await userRepo.DoesUserExist(dto.Phone))
				return Result.Failure("This number is already in use.");

			userRepo.AddUserDevice(dto);
			await userRepo.SaveChangesAsync();

			RequestSmsCodeDTO smsCodeDTO = new(dto.Phone, dto.DeviceId);

			await RequestSmsCode(smsCodeDTO);

			return Result.Success(HttpStatusCode.Created);
		}

		public async Task<Result> RequestSmsCode(RequestSmsCodeDTO dto)
		{
			if (EnvironmentHelper.IsDevelopment())
				return Result.Success(HttpStatusCode.OK);

			try
			{
				await VerificationResource.CreateAsync(appSettings.TwillioVerificationSid, dto.Phone, "sms");
			}
			catch (ApiException ex)
			{
				if (ex.Code == 60203)
					return Result.Failure("You can not send more codes at the moment. Please try again later.");

				if (ex.Code == 60200)
					return Result.Failure("Wrong phone number.");

				return Result.Failure("Failed to send sms code.");
			}

			return Result.Success(HttpStatusCode.OK);
		}

		/// <summary>
		/// Verifies the sms code of the user, generates a secret login code and returns it.
		/// </summary>
		/// <returns>A secret login code</returns>
		public async Task<Result<SecretLoginCodeDTO>> VerifySmsCode(VerifySmsCodeDTO dto)
		{
			User? user = await userRepo.GetUserIncludingDevicesAndLoginCode(dto.Phone);

			if (user is null)
				return Result<SecretLoginCodeDTO>.Failure("User with that phone is not found.", HttpStatusCode.NotFound);

			if (!EnvironmentHelper.IsDevelopment())
			{
				VerificationCheckResource verification =
					await VerificationCheckResource.CreateAsync(appSettings.TwillioVerificationSid, dto.SmsVerificationCode, dto.Phone);

				if (verification.Status == "canceled")
					return Result<SecretLoginCodeDTO>.Failure("Wrong sms verification code.");

				if (verification.Status == "pending")
					return Result<SecretLoginCodeDTO>.Failure("Sms code not verified. Try again.");
			}

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

				userDevice.UserLoginCode = new(user.Id, secretLoginCode);
				userDevice.IsVerified = true;

				await userRepo.SaveChangesAsync();
				return Result<SecretLoginCodeDTO>.Success(new(secretLoginCode));
			}

			if (userDevice.IsVerified)
				secretLoginCode = userDevice.UserLoginCode!.SecretLoginCode;

			if (!userDevice.IsVerified)
			{
				secretLoginCode = Guid.NewGuid();
				userDevice.UserLoginCode = new(user.Id, secretLoginCode);
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