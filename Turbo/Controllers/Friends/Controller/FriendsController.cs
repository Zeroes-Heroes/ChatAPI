using Database.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Services.Extensions;
using Services.Friendship.Interface;
using Services.Friendship.Models;
using Services.Utilities;
using System.ComponentModel.DataAnnotations;

namespace Turbo.Controllers.Friends.Controller;

[ApiController]
[Route("[controller]")]
[Authorize]
public class FriendsController(IFriendshipService friendshipService) : ControllerBase
{
	[HttpPost("add/{targetPhone}")]
	public async Task<ActionResult<FriendshipDTO>> AddFriend([FromRoute] string targetPhone)
	{
		Result<FriendshipDTO> result = await friendshipService.AddFriend(User.Id(), targetPhone);

		if (result.IsSuccess)
			return result.Data;

		return StatusCode(result.StatusCode, result.Error);
	}

	[HttpGet]
	public Task<IEnumerable<FriendshipDTO>> GetUserFriendships(FriendshipStatus? status, bool? isInitiator) =>
		friendshipService.GetUserFriendships(User.Id(), status, isInitiator);

	/// <summary>
	/// Acknowledges a friend request and updates the status of the friendship based on the parameters.
	/// </summary>
	/// <param name="userId">Id of the user who sent the friend request.</param>
	/// <param name="status">The new status to update with.</param>
	[HttpPost("request-respond")]
	public async Task<IActionResult> RespondToFriendRequest([Required] int userId, [Required] FriendshipStatus status)
	{
		Result result = await friendshipService.RespondToFriendRequest(userId, User.Id(), status);
		return StatusCode(result.StatusCode, result.Error);
	}
}