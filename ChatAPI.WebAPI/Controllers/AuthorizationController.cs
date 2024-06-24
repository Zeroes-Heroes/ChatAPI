using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Application.UseCases.Abstractions.Services;
using ChatAPI.Application.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatAPI.WebAPI.Controllers
{
	[ApiController]
	[Route("[controller]")]
	[Authorize]
	public class AuthorizationController(IUserService userService) : ControllerBase
	{
		[AllowAnonymous]
		[HttpPost("register")]
		public async Task<ActionResult> Register([FromBody] UserRegisterDTO payload)
		{
			Result result = await userService.Register(payload);
			return StatusCode(result.StatusCode, result.Error);
		}

		[AllowAnonymous]
		[HttpPost("request-sms-code")]
		public async Task<ActionResult> RequestSmsCode([FromBody] RequestSmsCodeDTO code)
		{
			Result result = await userService.RequestSmsCode(code);

			return StatusCode(result.StatusCode, result.Error);
		}

		[AllowAnonymous]
		[HttpPost("verify-sms-code")]
		public async Task<ActionResult<SecretLoginCodeDTO>> VerifySmsCode([FromBody] VerifySmsCodeDTO code)
		{
			Result<SecretLoginCodeDTO> result = await userService.VerifySmsCode(code);

			if (result.IsSuccess)
				return result.Data;

			return StatusCode(result.StatusCode, result.Error);
		}

		/// <summary>
		/// Logs in the user if the provided credentials are correct.
		/// Returns the access and refresh tokens.
		/// </summary>
		/// <param name="payload">Login credentials.</param>
		/// <returns>Access and refresh tokens.</returns>
		[AllowAnonymous]
		[HttpPost("login")]
		public async Task<ActionResult<UserLoginResponseDTO>> Login([FromBody] UserLoginDTO payload)
		{
			Result<UserLoginResponseDTO> result = await userService.Login(payload);

			if (result.IsSuccess)
				return result.Data;

			return StatusCode(result.StatusCode, result.Error);
		}
	}
}