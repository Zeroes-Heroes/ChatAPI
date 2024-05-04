using System.ComponentModel.DataAnnotations;
using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Enums;
using ChatAPI.WebAPI.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatAPI.WebAPI.Controllers
{
	[ApiController]
	[Route("[controller]")]
	[Authorize]
	public class FriendsController(IFriendshipService friendshipService) : ControllerBase
	{
		[HttpPost("add/{targetPhone}")]
		public async Task<IActionResult> AddFriend([FromRoute] string targetPhone)
		{
			Result result = await friendshipService.AddFriend(HttpContext.GetUserId(), targetPhone);

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