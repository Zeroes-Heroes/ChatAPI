using System.Security.Claims;
using Services.Membership.Models;

namespace Services.Membership.Interface;

/// <summary>
/// The one shared "what may this account context do with this chat" check (plan/07-membership.md:
/// "Общите проверки определят достъпа до съобщения и ключове за HTTP, socket и отложена доставка").
/// Every reachable surface asks THIS, never its own ad-hoc membership query:
/// <list type="bullet">
/// <item>HTTP - <see cref="Services.Chat.Interface.IChatService.GetChats"/> (which chats, and whether a
/// listed chat is a read-only archive).</item>
/// <item>Socket - <see cref="Services.Hubs.BaseHub"/>'s <c>send-message</c> (publish right, re-checked
/// under the chat lock inside the write transaction) and <c>chat-entered</c> (a frozen member marks
/// nothing Seen).</item>
/// <item>Deferred delivery - the reconnect sweep in <c>OnConnectedAsync</c> (which undelivered messages
/// may be delivered and acknowledged at all) and the push fan-out (checked immediately before sending,
/// per MESSAGE_SYNC_ANALYSIS.md).</item>
/// </list>
/// Stage 09 adds one more input to the same answer: for a PERSONAL chat it also asks
/// <see cref="Services.Contacts.Interface.IPersonalRelationDirectory"/> whether the two people may still
/// create new correspondence, and reports it as
/// <see cref="ChatAccess.PersonalCorrespondenceSuspended"/>. It narrows
/// <see cref="ChatAccess.CanSendNewMessages"/> only - reading and the status traffic of the messages
/// that already exist are deliberately untouched, and a group is never affected.
///
/// It composes the stage-06 <see cref="Services.Sessions.Interface.IAccountContextBoundary"/> so a
/// surface needs one call rather than two: a chat belonging to a superseded account context answers
/// <see cref="ChatAccessState.None"/> here, exactly as it did before.
///
/// Message keys: the same answer gates a message key grant. Stage 07 has no key-delivery path yet
/// (<see cref="Services.Keys.Interface.IMessageKeyProvisioningService.UnwrapForAuthorizedDelivery"/> has
/// no production caller), so stages 11-13 must call <see cref="GetForAccountAsync"/> (or
/// <see cref="GetAsync"/>) and check <see cref="ChatAccess.PermitsMessageAt"/> before unwrapping a key
/// - that is the "и ключове" half of the rule, and it is deliberately not stubbed here.
/// </summary>
public interface IChatAccessAuthority
{
	/// <summary>Access of the CALLER's own authenticated account context (never a user id alone).</summary>
	Task<ChatAccess> GetAsync(ClaimsPrincipal caller, int legacyChatId, CancellationToken ct = default);

	/// <summary>One round trip for a list surface; chats the caller cannot see at all are present with <see cref="ChatAccess.Denied"/>.</summary>
	Task<IReadOnlyDictionary<int, ChatAccess>> GetManyAsync(ClaimsPrincipal caller, IReadOnlyCollection<int> legacyChatIds, CancellationToken ct = default);

	/// <summary>
	/// Same answer as <see cref="GetAsync"/>, but first takes the chat's own
	/// <c>SyncChatSequenceCounters</c> row with <c>FOR UPDATE</c> inside the CALLER's already-open
	/// transaction - the exact row every membership mutation allocates its change sequence from
	/// (<see cref="Database.Sequencing.SyncSequenceAllocator"/>). That is what makes
	/// plan/07-membership.md's "Промените на правата се сериализират със записите в чата" true rather
	/// than hopeful: a send and a concurrent removal/leave contend for one row, so either the message
	/// commits before the removal is visible, or this call blocks until the removal commits and then
	/// re-reads the post-removal rights and is refused. Lock order is account -&gt; device -&gt; session
	/// (<see cref="Services.Sessions.Service.TrustedMutation"/>) -&gt; chat, the same order the membership
	/// mutations use, so the two can never deadlock against each other.
	/// </summary>
	/// <exception cref="InvalidOperationException">No explicit transaction is open on the shared DbContext.</exception>
	Task<ChatAccess> LockChatAndAuthorizeAsync(ClaimsPrincipal caller, int legacyChatId, CancellationToken ct = default);

	/// <summary>
	/// Access of a named account context, for paths that have resolved the account themselves (the
	/// membership writer, and the stage 11-13 key/delivery paths). Does not apply the fresh-start
	/// boundary, because an explicit AccountContextId already names exactly one context.
	/// </summary>
	Task<ChatAccess> GetForAccountAsync(string accountContextId, int legacyChatId, CancellationToken ct = default);

	/// <summary>
	/// Of <paramref name="candidateLegacyUserIds"/>, those whose CURRENT account context is an active
	/// member of this chat - i.e. who may receive new content. Used for live fan-out and for the push
	/// dispatch, so a removed or departed member stops receiving new messages immediately while keeping
	/// their archive. Legacy ids are what SignalR and the device-token table address, so this is the
	/// shape those two surfaces need; the fresh-start boundary is applied by them, as before.
	/// </summary>
	Task<IReadOnlyList<int>> ActiveLegacyRecipientsAsync(int legacyChatId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default);
}
