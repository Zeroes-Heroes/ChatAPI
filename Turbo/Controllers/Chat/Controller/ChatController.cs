using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Services.Chat.Interface;
using Services.Chat.Models.CreateChat;
using Services.Chat.Models.GetChats;
using Services.Chat.Models.Membership;
using Services.Extensions;
using Services.History.Interface;
using Services.History.Models;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Utilities;

namespace Turbo.Controllers.Chat.Controller
{
	[ApiController]
	[Route("[controller]")]
	[Authorize]
	public class ChatController(
		IChatService chatService, IChatMembershipService membershipService, IChatHistoryService historyService) : ControllerBase
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
			// Stage 08: the period travels as the adder's CHOICE plus its own UTC offset; the boundary it
			// means is resolved on the server, from the server clock, and intersected with what the adder
			// may itself read - a client cannot name a start of its own.
			SharedHistorySelection selection = new(request.SharedHistory, request.TimeZoneOffsetMinutes);

			Result<ChatMembershipChange> result = await membershipService.AddMembersAsync(User, request.ChatId, request.UserIds, selection, ct);
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

		/// <summary>
		/// Stage 08 (plan/08-history-archives.md): the one read of permitted history - newly shared older
		/// messages and frozen archives alike. Every rule (which windows, current state vs frozen snapshot)
		/// lives in <see cref="IChatHistoryService"/>; this action only carries the bounded page through.
		/// </summary>
		[HttpGet("history")]
		public async Task<ActionResult<ChatHistoryPage>> GetHistory(
			[FromQuery] int chatId, [FromQuery] int? beforeMessageId, [FromQuery] int limit, [FromQuery] bool missingOnThisDeviceOnly, CancellationToken ct)
		{
			Result<ChatHistoryPage> result = await historyService.ReadAsync(User, chatId, beforeMessageId, limit, missingOnThisDeviceOnly, ct);
			return result.IsSuccess ? result.Data : StatusCode(result.StatusCode, result.Error);
		}

		/// <summary>
		/// The technical confirmation that this device stored archived messages. It updates the private
		/// per-device record only - the public archive marks and unread stay untouched.
		/// </summary>
		[HttpPost("history-stored")]
		public async Task<ActionResult<ArchiveStoreResult>> ConfirmHistoryStored(ConfirmArchiveStoredRequest request, CancellationToken ct)
		{
			Result<ArchiveStoreResult> result = await historyService.ConfirmArchiveStoredAsync(User, request.ChatId, request.MessageIds, ct);
			return result.IsSuccess ? result.Data : StatusCode(result.StatusCode, result.Error);
		}

		private ActionResult<ChatMembershipChange> Translate(Result<ChatMembershipChange> result) =>
			result.IsSuccess ? result.Data : StatusCode(result.StatusCode, result.Error);
	}
}
