namespace Services.Utilities.Statics;

public static class LiveEvents
{
	public static readonly string NewMessage = "new-message";
	public static readonly string FriendRequestAnswer = "friend-request-answer";
	public static readonly string NewFriendRequest = "friend-request";
	public static readonly string ChatCreated = "chat-created";
	public static readonly string NewMessageCreated = "new-message-created";
	public static readonly string MessageStatusUpdate = "message-status-update";

	/// <summary>Client event name for a wire SessionControlEvent, sent to <see cref="SessionGroupFormat"/>'s group - see Services/Sessions/Service/SessionRevocationNotifier.cs.</summary>
	public static readonly string SessionControl = "session-control";

	/// <summary>SignalR group name format every connection for a given SessionId joins in BaseHub.OnConnectedAsync, and the only scope SessionRevocationNotifier ever sends to - plan/05-device-sessions.md requires revocation to target exactly that device's own connections, never the whole account/user.</summary>
	public static readonly string SessionGroupFormat = "session:{0}";
}
