namespace Services.Chat.Models.Membership;

/// <summary>Stage 07 group composition requests. Chat ids are the legacy ids every current client surface speaks; the service maps them to the sync model itself.</summary>
public class AddChatMembersRequest
{
	public int ChatId { get; set; }

	public int[] UserIds { get; set; } = [];
}

public class RemoveChatMemberRequest
{
	public int ChatId { get; set; }

	public int UserId { get; set; }
}

public class LeaveChatRequest
{
	public int ChatId { get; set; }
}
