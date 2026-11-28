using System.Security.Claims;

namespace Services.Sessions.Interface;

/// <summary>
/// The fresh-start boundary at the legacy surfaces. The legacy chat/message tables key on the legacy
/// user id alone, so a NEW account context for the same phone would otherwise still read the old
/// context's chats (plan/06-recovery.md: a new beginning is "без стара история"). This is the one place
/// that answers "may this caller's context touch this legacy chat", used by the real reachable surfaces
/// (<see cref="Services.Chat.Interface.IChatService"/> and the hub), not only by new sync tables.
///
/// For a first/only context - every account that has never done a fresh start - every method here is a
/// no-op, so nothing about existing behaviour changes.
/// </summary>
public interface IAccountContextBoundary
{
	/// <summary>Legacy chat ids the caller's current context must not see, or an empty set when it is not a fresh-start context.</summary>
	Task<IReadOnlySet<int>> ExcludedLegacyChatIdsAsync(ClaimsPrincipal caller, CancellationToken ct = default);

	/// <summary>Same question for one chat; used by the publish/enter paths that act on a single id.</summary>
	Task<bool> IsLegacyChatExcludedAsync(ClaimsPrincipal caller, int legacyChatId, CancellationToken ct = default);

	/// <summary>
	/// Recipient-side isolation for live delivery. SignalR addresses users by legacy user id, so a fresh
	/// context would otherwise receive the OLD context's chat events over its own connection (same
	/// NameIdentifier). Returns who may be addressed by user id and, for any recipient whose phone also
	/// has a context that excludes this chat, the exact connections that may receive it.
	/// </summary>
	Task<LegacyRecipients> ResolveLegacyRecipientsAsync(IReadOnlyCollection<int> legacyUserIds, int legacyChatId, CancellationToken ct = default);

	/// <summary>
	/// Recipient-side isolation for PUSH delivery, which carries the message text itself. Device tokens
	/// are registered per legacy user id (<see cref="Database.Entities.DeviceNotificationConfigEntity"/>)
	/// with no account context, so the only thing decidable here is the phone's CURRENT context: if that
	/// one must not see the chat, no notification for it may leave. Returns the ids that may be notified.
	/// </summary>
	Task<IReadOnlyList<int>> NotifiableLegacyUserIdsAsync(IReadOnlyCollection<int> legacyUserIds, int legacyChatId, CancellationToken ct = default);
}

/// <summary>
/// <paramref name="UserIds"/> may be addressed with the ordinary user-id fan-out (no context on that
/// phone excludes this chat). <paramref name="ConnectionIds"/> are the individually-addressable
/// connections of the remaining recipients that ARE allowed this chat.
/// </summary>
/// <param name="UserIds">Recipients with no excluded context.</param>
/// <param name="ConnectionIds">Allowed connections of recipients that have an excluded context, on this replica only - see the implementation's note.</param>
public sealed record LegacyRecipients(IReadOnlyList<int> UserIds, IReadOnlyList<string> ConnectionIds);
