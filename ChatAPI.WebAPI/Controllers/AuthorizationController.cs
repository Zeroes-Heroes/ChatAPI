using ChatAPI.Application.DTOs.User;
using ChatAPI.Application.Models.Authorization;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Application.Utilities;
using ChatAPI.WebAPI.Models.User.Request;
using ChatAPI.WebAPI.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatAPI.WebAPI.Controllers
{
	[ApiController]
	[Route("[controller]")]
	[Authorize]
	public class AuthorizationController(IUserService userService, ITokenService tokenService) : ControllerBase
	{
		[AllowAnonymous]
		[HttpPost("register")]
		public Task Register([FromBody] UserRegisterDTO payload) =>
			userService.Register(payload);

		[AllowAnonymous]
		[HttpPost("login")]
		public async Task<ActionResult<TokensDTO>> Login([FromBody] UserLoginDTO payload)
		{
			Result<TokensDTO> result = await userService.Login(payload);

			if (!result.IsSuccess)
				return StatusCode(result.StatusCode, result.Error);

			return result.Data;
		}

		[HttpPost("tokens/refresh")]
		public async Task<ActionResult<TokensDTO>> RefreshTokens([FromBody] RefreshTokenRequestModel payload)
		{
			if (!await tokenService.ValidateRefreshToken(payload.RefreshToken))
				return Unauthorized();

			int userId = HttpContext.GetUserId();
			await userService.Logout(userId);
			return await tokenService.GenerateTokens(userId);
		}

		[HttpPost("logout")]
		public Task Logout() =>
			userService.Logout(HttpContext.GetUserId());
	}
}