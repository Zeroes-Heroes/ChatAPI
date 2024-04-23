using System.ComponentModel.DataAnnotations;
using Application.DTOs.Friends;
using Application.UseCases.Abstractions;
using Application.Utilities;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAPI.Modules;

namespace WebAPI.Controllers
{
	[ApiController]
	[Route("[controller]")]
	[Authorize]
	public class FriendsController(IFriendshipService friendshipService) : ControllerBase
	{
		[HttpPost("add/{targetEmail}")]
		public async Task<IActionResult> AddFriend([FromRoute] string targetEmail)
		{
			Result result = await friendshipService.AddFriend(HttpContext.GetUserId(), targetEmail);

			if (!result.IsSuccess)
				return StatusCode(result.StatusCode, result.Error);

			return StatusCode(result.StatusCode);
		}

		[HttpGet]
		public Task<IEnumerable<FriendDTO>> GetUserFriendships(FriendshipStatus? status, bool? isInitiator) =>
			friendshipService.GetUserFriendships(HttpContext.GetUserId(), status, isInitiator);

		[HttpPost("request/respond")]
		public Task RespondToFriendRequest([Required] int friendUserId, [Required] FriendshipStatus status) =>
			friendshipService.RespondToFriendRequest(friendUserId, HttpContext.GetUserId(), status);
	}
}