namespace Services.Utilities.Statics;

public static class LiveEvents
{
	public static readonly string NewMessage = "new-message";
	public static readonly string FriendRequestAnswer = "friend-request-answer";
	public static readonly string NewFriendRequest = "friend-request";
	public static readonly string ChatCreated = "chat-created";

	/// <summary>Client event name for a committed group composition change (stage 07) - see Services/Membership/Service/ChatMembershipService.cs. A removed subject receives its own copy of this event without the actor's identity.</summary>
	public static readonly string ChatMembershipChanged = "chat-membership-changed";
	/// <summary>
	/// Client event name for a committed personal-relation change (stage 09, plan/09-contacts-privacy.md)
	/// - a block, an unblock or a contact removal. Both parties receive it, each with the copy that is
	/// true for IT: the same projection <c>GET /friends</c> returns, so a device sees the same relation
	/// and the same profile data over SignalR as over HTTP.
	/// </summary>
	public static readonly string PersonalRelationChanged = "personal-relation-changed";
	public static readonly string NewMessageCreated = "new-message-created";
	public static readonly string MessageStatusUpdate = "message-status-update";

	/// <summary>Client event name for a wire SessionControlEvent, sent to <see cref="SessionGroupFormat"/>'s group - see Services/Sessions/Service/SessionRevocationNotifier.cs.</summary>
	public static readonly string SessionControl = "session-control";

	/// <summary>SignalR group name format every connection for a given SessionId joins in BaseHub.OnConnectedAsync, and the only scope SessionRevocationNotifier ever sends to - plan/05-device-sessions.md requires revocation to target exactly that device's own connections, never the whole account/user.</summary>
	public static readonly string SessionGroupFormat = "session:{0}";
}
