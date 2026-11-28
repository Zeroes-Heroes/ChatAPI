using Database.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Services.Contacts.Interface;
using Services.Extensions;
using Services.Friendship.Interface;
using Services.Friendship.Models;
using Services.Utilities;
using System.ComponentModel.DataAnnotations;

namespace Turbo.Controllers.Friends.Controller;

[ApiController]
[Route("[controller]")]
[Authorize]
public class FriendsController(IFriendshipService friendshipService, IPersonalRelationService personalRelations) : ControllerBase
{
	[HttpPost("add/{targetPhone}")]
	public async Task<ActionResult<FriendshipDTO>> AddFriend([FromRoute] string targetPhone)
	{
		Result<FriendshipDTO> result = await friendshipService.AddFriend(User.Id(), targetPhone, User);

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
		Result result = await friendshipService.RespondToFriendRequest(userId, User.Id(), status, User);
		return StatusCode(result.StatusCode, result.Error);
	}

	/// <summary>
	/// Stage 09 personal relations (plan/09-contacts-privacy.md). Each action is a thin translation of
	/// one <see cref="IPersonalRelationService"/> outcome - every rule, the trust re-check at the moment
	/// of the write and both live copies live in that service. The committed relation is returned so the
	/// caller's own UI applies it without waiting for its `personal-relation-changed` event.
	///
	/// Unlike the legacy status endpoint above, these are available to EITHER party: an accepted contact
	/// could previously only be acted on by the person who received the original request.
	/// </summary>
	[HttpPost("block")]
	public Task<ActionResult<PersonalRelationChange>> Block([Required] int userId, CancellationToken ct) =>
		Translate(personalRelations.BlockAsync(User, userId, ct));

	[HttpPost("unblock")]
	public Task<ActionResult<PersonalRelationChange>> Unblock([Required] int userId, CancellationToken ct) =>
		Translate(personalRelations.UnblockAsync(User, userId, ct));

	[HttpPost("remove")]
	public Task<ActionResult<PersonalRelationChange>> RemoveContact([Required] int userId, CancellationToken ct) =>
		Translate(personalRelations.RemoveContactAsync(User, userId, ct));

	private async Task<ActionResult<PersonalRelationChange>> Translate(Task<Result<PersonalRelationChange>> pending)
	{
		Result<PersonalRelationChange> result = await pending;
		return result.IsSuccess ? result.Data : StatusCode(result.StatusCode, result.Error);
	}
}
