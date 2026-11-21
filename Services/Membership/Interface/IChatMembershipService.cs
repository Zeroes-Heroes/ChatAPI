using System.Security.Claims;
using Services.Membership.Models;
using Services.Utilities;

namespace Services.Membership.Interface;

/// <summary>
/// The only writer of the chat/membership model (plan/07-membership.md). Owns chat creation's
/// membership rules and the three composition mutations, each of them committed under
/// <see cref="Services.Sessions.Service.TrustedMutation"/> (so a concurrent revoke of the caller
/// serializes against the write) and under the chat's own counter-row lock (so a rights change
/// serializes against the chat's message writes).
///
/// A chat's sync id is the decimal string of its legacy <c>Chats.Id</c>, per docs/sync-contract-v1.md
/// ("Existing numeric entity IDs become decimal strings"), which is what keeps one ChatId - and one
/// <see cref="Database.Enums.Sync.SyncChatKind"/> - across every composition change, down to one or
/// zero active members.
/// </summary>
public interface IChatMembershipService
{
	/// <summary>
	/// Creation rules that must hold at the moment of the insert, not merely when the request arrived:
	/// no existing chat already has exactly this active participant set (regardless of its name), and
	/// nobody being invited has blocked the creator. Called by
	/// <see cref="Services.Chat.Interface.IChatService.CreateChat"/> INSIDE its own transaction, just
	/// before the legacy chat row is inserted; it takes a transaction-scoped advisory lock derived from
	/// the participant set, so two concurrent creations of the same set cannot both pass.
	/// </summary>
	/// <exception cref="DuplicateChatMemberSetException">A chat with exactly this active participant set already exists.</exception>
	/// <exception cref="ChatMembershipForbiddenException">A participant has blocked the creator.</exception>
	/// <exception cref="InvalidOperationException">No explicit transaction is open on the shared DbContext.</exception>
	Task GuardChatCreationAsync(int creatorLegacyUserId, IReadOnlyCollection<int> memberLegacyUserIds, CancellationToken ct = default);

	/// <summary>
	/// Projects a just-inserted legacy chat into the sync model: the chat with its kind (exactly two
	/// participants = Direct, more = Group), its sequence counter, one active stint per participant with
	/// full history of this chat, the chat-change journal entries and each participant's catalog entry.
	/// Called inside the same transaction as the legacy insert, so the chat and its membership commit
	/// together or not at all.
	/// </summary>
	Task ProjectCreatedChatAsync(int legacyChatId, string? chatName, IReadOnlyCollection<int> memberLegacyUserIds, CancellationToken ct = default);

	/// <summary>
	/// Adds participants to a group, effective immediately and without any invitation being accepted.
	/// Refused for a direct chat (it keeps its personal type), for a caller without an active stint, for
	/// a participant who is already active (an active member is never re-added to widen their history),
	/// and for a participant who has blocked the caller. New participants start with no previous history
	/// - choosing a shared period is stage 08.
	/// </summary>
	Task<Result<ChatMembershipChange>> AddMembersAsync(ClaimsPrincipal actingUser, int legacyChatId, IReadOnlyCollection<int> targetLegacyUserIds, CancellationToken ct = default);

	/// <summary>
	/// Removes one participant from a group. Any active participant may remove any other, with the same
	/// rights boundary as leaving; removing yourself IS leaving and is recorded as such. The removed
	/// participant keeps a read-only archive of what their stint permitted.
	/// </summary>
	Task<Result<ChatMembershipChange>> RemoveMemberAsync(ClaimsPrincipal actingUser, int legacyChatId, int targetLegacyUserId, CancellationToken ct = default);

	/// <summary>Leaves a group voluntarily; recorded distinguishably from a removal so even a freshly restored device can tell the two apart.</summary>
	Task<Result<ChatMembershipChange>> LeaveAsync(ClaimsPrincipal actingUser, int legacyChatId, CancellationToken ct = default);

	/// <summary>
	/// Membership-derived view of chats for a list surface: the chat's persisted kind (never a guess
	/// from the current member count - a group with two, one or zero active members is still a group),
	/// whether this caller only holds a read-only archive, and the participants this caller may see -
	/// the active set for an active stint, the composition as of the removal for a frozen one. Chats
	/// that have no sync projection yet (created before this stage) are absent from the result and must
	/// keep their legacy behaviour at the call site.
	/// </summary>
	Task<IReadOnlyDictionary<int, ChatMembershipView>> DescribeAsync(ClaimsPrincipal caller, IReadOnlyCollection<int> legacyChatIds, CancellationToken ct = default);
}

/// <param name="MemberLegacyUserIds">Who this caller may see as participants - frozen stints see the composition as of their own removal, never later changes.</param>
public sealed record ChatMembershipView(bool IsGroup, bool IsArchived, IReadOnlyList<int> MemberLegacyUserIds);
