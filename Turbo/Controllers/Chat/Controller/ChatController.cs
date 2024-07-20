using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Services.Chat.Interface;
using Services.Chat.Models.CreateChat;
using Services.Chat.Models.GetChats;
using Services.Extensions;
using Services.Utilities;

namespace Turbo.Controllers.Chat.Controller
{
	[ApiController]
	[Route("[controller]")]
	[Authorize]
	public class ChatController(IChatService chatService) : ControllerBase
	{
		[HttpPost("create-chat")]
		public async Task<IActionResult> CreateChat(CreateChatRequest createChatRequestModel)
		{
			Result result = await chatService.CreateChat(createChatRequestModel);

			return StatusCode(result.StatusCode, result.Error);
		}

		[HttpGet("get-chats")]
		public async Task<ActionResult<GetChatsResponse[]>> GetChats()
		{
			Result<GetChatsResponse[]> result = await chatService.GetChats(User.Id());

			if (result.IsSuccess)
				return result.Data;

			return StatusCode(result.StatusCode, result.Error);
		}
	}
}
