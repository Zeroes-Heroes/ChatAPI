namespace Services.Chat.Models.GetChats;

public record GetChatsResponse(int Id, string Name, int[] UserIds);
