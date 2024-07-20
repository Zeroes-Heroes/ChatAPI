namespace Services.Chat.Models.GetChats;

public record GetChatsResponse(int ChatId, string ChatName, int[] UserIds);
