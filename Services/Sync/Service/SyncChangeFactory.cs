using Services.Sync.Contracts;
using Services.Sync.Models;
using static Services.Sync.Service.SyncWire;

namespace Services.Sync.Service;

/// <summary>
/// Builds the one-payload-per-change items a page and a live event share. Every factory here fills
/// exactly one payload and leaves the rest null, which is the contract's own rule ("Exactly one
/// nullable payload is populated and matches Kind"), and takes the id from <see cref="SyncEventId"/> so
/// the same fact carries the same id whichever surface delivered it.
/// </summary>
internal static class SyncChangeFactory
{
	public static SyncChange Message(SyncScopeContext scope, MessageDelivery delivery, long position) =>
		new(SyncEventId.Message(scope.ChatId, position),
			ChatPosition(scope.ChatId, scope.MembershipId, position),
			scope.Scope, ChangeKind.Message,
			Chat: null, Message: delivery, RecipientState: null, DeviceState: null, Unread: null, Membership: null, MembershipEvent: null);

	public static SyncChange RecipientState(SyncScopeContext scope, MessageRecipientState state, long position) =>
		new(SyncEventId.RecipientState(scope.ChatId, state.MessageId, state.RecipientAccountContextId, state.Version),
			ChatPosition(scope.ChatId, scope.MembershipId, position),
			scope.Scope, ChangeKind.RecipientState,
			Chat: null, Message: null, RecipientState: state, DeviceState: null, Unread: null, Membership: null, MembershipEvent: null);

	public static SyncChange DeviceState(SyncScopeContext scope, MessageDeviceState state, long position) =>
		new(SyncEventId.DeviceState(scope.ChatId, state.MessageId, state.Device.LocalInstanceId, state.Version),
			ChatPosition(scope.ChatId, scope.MembershipId, position),
			scope.Scope, ChangeKind.DeviceState,
			Chat: null, Message: null, RecipientState: null, DeviceState: state, Unread: null, Membership: null, MembershipEvent: null);

	public static SyncChange Unread(SyncScopeContext scope, UnreadState unread, long position) =>
		new(SyncEventId.Unread(scope.ChatId, scope.MembershipId, unread.Version),
			ChatPosition(scope.ChatId, scope.MembershipId, position),
			scope.Scope, ChangeKind.Unread,
			Chat: null, Message: null, RecipientState: null, DeviceState: null, Unread: unread, Membership: null, MembershipEvent: null);

	public static SyncChange Membership(SyncScopeContext scope, MembershipSnapshot snapshot, long position) =>
		new(SyncEventId.Membership(scope.ChatId, snapshot.Scope.MembershipId, snapshot.Version),
			ChatPosition(scope.ChatId, scope.MembershipId, position),
			scope.Scope, ChangeKind.Membership,
			Chat: null, Message: null, RecipientState: null, DeviceState: null, Unread: null, Membership: snapshot, MembershipEvent: null);

	// Named MembershipEventChange, not MembershipEvent: a method whose name equals the contract type its
	// own parameter uses would hide that type inside this class.
	public static SyncChange MembershipEventChange(SyncScopeContext scope, string membershipId, MembershipEvent change, long position) =>
		new(SyncEventId.MembershipEvent(scope.ChatId, membershipId, change.Kind.ToString()),
			ChatPosition(scope.ChatId, scope.MembershipId, position),
			scope.Scope, ChangeKind.MembershipEvent,
			Chat: null, Message: null, RecipientState: null, DeviceState: null, Unread: null, Membership: null, MembershipEvent: change);

	/// <summary>A chat snapshot, positioned in whichever stream carried it - the catalog for a catalog page, the chat's own stream for a chat change.</summary>
	public static SyncChange Chat(SyncScopeContext scope, ChatSnapshot chat, StreamPosition position) =>
		new(SyncEventId.Chat(chat.ChatId, Version(chat.Version)),
			position,
			scope.Scope, ChangeKind.Chat,
			Chat: chat, Message: null, RecipientState: null, DeviceState: null, Unread: null, Membership: null, MembershipEvent: null);

	private static long Version(string version) => TryParseSeq(version, out long parsed) ? parsed : 0;
}
