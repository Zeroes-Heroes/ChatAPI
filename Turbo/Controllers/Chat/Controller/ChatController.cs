using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Services.Chat.Interface;
using Services.Chat.Models.CreateChat;
using Services.Chat.Models.GetChats;
using Services.Chat.Models.Membership;
using Services.Extensions;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Utilities;

namespace Turbo.Controllers.Chat.Controller
{
	[ApiController]
	[Route("[controller]")]
	[Authorize]
	public class ChatController(IChatService chatService, IChatMembershipService membershipService) : ControllerBase
	{
		[HttpPost("create-chat")]
		public async Task<IActionResult> CreateChat(CreateChatRequest createChatRequestModel)
		{
			Result result = await chatService.CreateChat(createChatRequestModel, User.Id(), User);
			return StatusCode(result.StatusCode, result.Error);
		}

		[HttpGet("get-chats")]
		public async Task<ActionResult<GetChatsResponse[]>> GetChats()
		{
			Result<GetChatsResponse[]> result = await chatService.GetChats(User.Id(), User);

			if (result.IsSuccess)
				return result.Data;

			return StatusCode(result.StatusCode, result.Error);
		}

		/// <summary>
		/// Stage 07 group composition (plan/07-membership.md). Each action is a thin translation of one
		/// <see cref="IChatMembershipService"/> outcome - every rule, lock and broadcast lives in that
		/// service, which re-validates the caller's own trust at the moment of the write. The committed
		/// change is returned so the caller's own UI applies it without waiting for its live event.
		/// </summary>
		[HttpPost("add-members")]
		public async Task<ActionResult<ChatMembershipChange>> AddMembers(AddChatMembersRequest request, CancellationToken ct)
		{
			Result<ChatMembershipChange> result = await membershipService.AddMembersAsync(User, request.ChatId, request.UserIds, ct);
			return Translate(result);
		}

		[HttpPost("remove-member")]
		public async Task<ActionResult<ChatMembershipChange>> RemoveMember(RemoveChatMemberRequest request, CancellationToken ct)
		{
			Result<ChatMembershipChange> result = await membershipService.RemoveMemberAsync(User, request.ChatId, request.UserId, ct);
			return Translate(result);
		}

		[HttpPost("leave")]
		public async Task<ActionResult<ChatMembershipChange>> Leave(LeaveChatRequest request, CancellationToken ct)
		{
			Result<ChatMembershipChange> result = await membershipService.LeaveAsync(User, request.ChatId, ct);
			return Translate(result);
		}

		private ActionResult<ChatMembershipChange> Translate(Result<ChatMembershipChange> result) =>
			result.IsSuccess ? result.Data : StatusCode(result.StatusCode, result.Error);
	}
}
