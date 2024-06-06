using System.ComponentModel.DataAnnotations;
using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.UseCases.Abstractions.Services;
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
			return StatusCode(result.StatusCode, result.Error);
		}

		[HttpGet]
		public Task<IEnumerable<FriendshipDTO>> GetUserFriendships(FriendshipStatus? status, bool? isInitiator) =>
			friendshipService.GetUserFriendships(HttpContext.GetUserId(), status, isInitiator);

		/// <summary>
		/// Acknowledges a friend request and updates the status of the friendship based on the parameters.
		/// </summary>
		/// <param name="userId">Id of the user who sent the friend request.</param>
		/// <param name="status">The new status to update with.</param>
		[HttpPost("request-respond")]
		public async Task<IActionResult> RespondToFriendRequest([Required] int userId, [Required] FriendshipStatus status)
		{
			Result result = await friendshipService.RespondToFriendRequest(userId, HttpContext.GetUserId(), status);
			return StatusCode(result.StatusCode, result.Error);
		}
	}
}