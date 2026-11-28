using System.Security.Claims;
using Services.Chat.Models.CreateChat;
using Services.Chat.Models.Events;
using Services.Chat.Models.GetChats;
using Services.Utilities;

namespace Services.Chat.Interface;

public interface IChatService
{
	/// <param name="actingUser">The caller's own authenticated principal - re-validated as currently trusted (session/device/trust-version) immediately before the chat is actually inserted and broadcast to the other participants, not merely at the controller's own authorization-on-entry (see Services.Sessions.Service.TrustedMutation's own doc comment). Stage 07: the same transaction also applies the creation rules (no duplicate active participant set, no participant that has blocked the creator) and projects the chat's membership (see Services.Membership.Interface.IChatMembershipService).</param>
	Task<Result> CreateChat(CreateChatRequest createChatRequestModel, int chatCreatorId, ClaimsPrincipal actingUser);
	/// <param name="actingUser">The caller's own principal - its AccountContextId decides both the fresh-start legacy boundary and the membership rights, through the one shared check (<see cref="Services.Membership.Interface.IChatAccessAuthority"/>); a user id alone cannot.</param>
	Task<Result<GetChatsResponse[]>> GetChats(int userId, ClaimsPrincipal actingUser);
	void SendEventChatCreated(ChatCreatedEvent chatCreatedEventModel);
}
