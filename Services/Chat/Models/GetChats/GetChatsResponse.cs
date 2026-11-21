namespace Services.Chat.Models.GetChats;

/// <param name="UserIds">The participants this caller may see: the active ones, or - for a chat this caller only holds as a frozen archive - the composition as of its own removal.</param>
/// <param name="IsGroup">The chat's own persisted kind (stage 07), never a guess from the participant count: a group that has lost members down to two, one or zero active participants is still a group, and a personal chat never becomes one.</param>
/// <param name="IsArchived">True when this caller's membership has ended: the chat is a read-only archive for it (no sending, no new content).</param>
/// <param name="CanSendNewMessages">
/// Stage 09: false when no NEW message may be created here - a read-only archive, or a personal chat
/// whose two people blocked each other or stopped being contacts. The permitted history of a suspended
/// personal chat stays readable and keeps receiving status updates, which is why this is separate from
/// <paramref name="IsArchived"/> rather than folded into it.
/// </param>
public record GetChatsResponse(int ChatId, string ChatName, int[] UserIds, bool IsGroup, bool IsArchived, bool CanSendNewMessages);
