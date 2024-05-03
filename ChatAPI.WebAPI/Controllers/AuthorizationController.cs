using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Application.UseCases.Abstractions;
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

			if (!result.IsSuccess)
				return StatusCode(result.StatusCode);

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
	}
}