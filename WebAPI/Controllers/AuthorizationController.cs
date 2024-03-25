using Application.Models.Authorization;
using Application.UseCases.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAPI.Models.User.Request;
using WebAPI.Modules;

namespace WebAPI.Controllers
{
	[ApiController]
	[Route("[controller]")]
	[Authorize]
	public class AuthorizationController(IUserService userService, ITokenService tokenService) : ControllerBase
	{
		[AllowAnonymous]
		[HttpPost("register")]
		public Task Register([FromBody] AuthRequestModel payload) =>
			userService.Register(payload.Email, payload.Password);

		[AllowAnonymous]
		[HttpPost("login")]
		public async Task<ActionResult<TokensDTO>> Login([FromBody] AuthRequestModel payload)
		{
			TokensDTO? tokens = await userService.Login(payload.Email, payload.Password);
			
			if (tokens is null)
				return Unauthorized();

			return tokens;
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