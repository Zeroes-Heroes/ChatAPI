using System.Net;
using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Database.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Contacts.Interface;
using Services.Contacts.Models;
using Services.Extensions;
using Services.Hubs;
using Services.NotificationDispatch.Interface;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Utilities;
using static Services.Utilities.Statics.LiveEvents;

namespace Services.Contacts.Service;

/// <summary>
/// See <see cref="IPersonalRelationService"/>. The only writer of <c>SyncPersonalBlocks</c>, and the
/// only place a contact removal is written.
///
/// Locking: this module takes no chat lock and allocates no sequence, so it adds nothing to stage 07's
/// account -&gt; device -&gt; session -&gt; chat -&gt; catalog order; it stops at the device/session row
/// <see cref="TrustedMutation"/> already takes. A send racing a block still has exactly one
/// serialization point - <c>BaseHub.SendMessage</c>'s <c>LockChatAndAuthorizeAsync</c>, which re-reads
/// the rights (now including this block) under the chat's own counter row - so either the message
/// commits before the block is visible, or the send waits and is then refused.
/// </summary>
public sealed class PersonalRelationService(
	AppDbContext db,
	IPersonalRelationDirectory relations,
	IProfileVisibility profiles,
	ISessionTrustChecker trustChecker,
	IHubContext<BaseHub> hubContext,
	INotificationDispatch notificationDispatch,
	ILogger<PersonalRelationService> logger,
	TimeProvider timeProvider) : IPersonalRelationService
{
	public async Task<Result<PersonalRelationChange>> BlockAsync(ClaimsPrincipal actingUser, int subjectLegacyUserId, CancellationToken ct = default)
	{
		Result<PersonalRelationChange> result = await BlockCoreAsync(actingUser, subjectLegacyUserId, ct);
		if (!result.IsSuccess)
		{
			return result;
		}

		try
		{
			// Kept from the legacy block path so the existing notification does not silently disappear -
			// but the dispatcher now resolves the actor's name through IProfileVisibility, and a blocked
			// party no longer holds a disclosing relation with its blocker, so what it receives is the
			// "unknown" placeholder rather than a name.
			await notificationDispatch.NotificationForBlockedFriendship(subjectLegacyUserId, actingUser.Id());
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to dispatch the block notification for user {UserId}", subjectLegacyUserId);
		}

		return result;
	}

	private Task<Result<PersonalRelationChange>> BlockCoreAsync(ClaimsPrincipal actingUser, int subjectLegacyUserId, CancellationToken ct) =>
		MutateAsync(actingUser, subjectLegacyUserId, (actorId, now, token) =>
			// ONE conflict-tolerant statement rather than read-then-insert. TrustedMutation locks the
			// caller's DEVICE row, which serializes two attempts from the same device but NOT two devices
			// of the same account blocking the same person at the same moment - and a retry after a lost
			// response is routinely sent while the first attempt may still be in flight. A read-then-insert
			// lets both sides read "no row" and the loser fail on PK_SyncPersonalBlocks, i.e. a 500 for what
			// is by definition the same outcome. Losing that race IS the idempotent success this operation
			// promises, because the row the racer wrote is identical to this one's.
			//
			// CreatedAtUtc is preserved while the block is already in force, so a retry cannot move the
			// moment the block started; only re-blocking a LIFTED row re-dates it.
			db.Database.ExecuteSqlInterpolatedAsync(
				$"""
				INSERT INTO "SyncPersonalBlocks" ("BlockerUserId", "SubjectUserId", "CreatedAtUtc", "LiftedAtUtc")
				VALUES ({actorId}, {subjectLegacyUserId}, {now}, NULL)
				ON CONFLICT ("BlockerUserId", "SubjectUserId") DO UPDATE
				SET "LiftedAtUtc" = NULL,
				    "CreatedAtUtc" = CASE WHEN "SyncPersonalBlocks"."LiftedAtUtc" IS NULL
				                          THEN "SyncPersonalBlocks"."CreatedAtUtc"
				                          ELSE EXCLUDED."CreatedAtUtc" END
				""", token), ct);

	public Task<Result<PersonalRelationChange>> UnblockAsync(ClaimsPrincipal actingUser, int subjectLegacyUserId, CancellationToken ct = default) =>
		MutateAsync(actingUser, subjectLegacyUserId, async (actorId, now, token) =>
		{
			// A conditional UPDATE, so two concurrent lifts converge on one committed time instead of
			// racing: the second one matches no row and writes nothing. No insert, so no conflict either.
			await db.Database.ExecuteSqlInterpolatedAsync(
				$"""
				UPDATE "SyncPersonalBlocks" SET "LiftedAtUtc" = {now}
				WHERE "BlockerUserId" = {actorId} AND "SubjectUserId" = {subjectLegacyUserId} AND "LiftedAtUtc" IS NULL
				""", token);

			// A block written BEFORE this stage lives on the legacy row as Status = Blocked, with the
			// caller as its TargetUserId. Lifting the new row alone would leave that one in force, so the
			// legacy status is restored to Accepted - the status a legacy block normally replaced, and the
			// only one that makes "Отблокирането възстановява достъпа" true.
			//
			// The replaced status was never recorded, and the legacy endpoint did NOT guarantee it was
			// Accepted: its [CHAT-23] guard is commented out (FriendshipService.RespondToRequest), so
			// Rejected -> Blocked was reachable too, and lifting such a row restores more than it replaced.
			// That is not a privilege escalation - the caller is the row's TargetUserId and can set
			// Accepted outright through POST /friends/request-respond - and it is bounded: no NEW legacy
			// Blocked row can be written any more, because that endpoint now routes Blocked to BlockAsync.
			// Recording the replaced status is deliberately not introduced for this finite historical set;
			// blocks written from this stage on carry no legacy status at all.
			FriendshipEntity? legacyBlock = await db.Friendships
				.FirstOrDefaultAsync(f => f.Status == FriendshipStatus.Blocked
					&& f.TargetUserId == actorId && f.SenderUserId == subjectLegacyUserId, token);

			if (legacyBlock is not null)
			{
				legacyBlock.Status = FriendshipStatus.Accepted;
			}

			await db.SaveChangesAsync(token);
		}, ct);

	public Task<Result<PersonalRelationChange>> RemoveContactAsync(ClaimsPrincipal actingUser, int subjectLegacyUserId, CancellationToken ct = default) =>
		MutateAsync(actingUser, subjectLegacyUserId, async (actorId, now, token) =>
		{
			// Either party may remove - the legacy endpoint only ever let the request's TARGET change a
			// status, which is why an accepted contact could not be dropped by the person who requested it.
			FriendshipEntity? relation = await db.Friendships
				.FirstOrDefaultAsync(f => (f.SenderUserId == actorId && f.TargetUserId == subjectLegacyUserId)
					|| (f.SenderUserId == subjectLegacyUserId && f.TargetUserId == actorId), token);

			if (relation is not null && relation.Status != FriendshipStatus.Rejected)
			{
				// Rejected is the legacy "no current contact, may be requested again" state, which is what
				// "Премахнат контакт може да бъде добавен повторно" needs and what AddFriend already honours.
				// The block overlay, if any, is deliberately untouched: removing is not unblocking.
				relation.Status = FriendshipStatus.Rejected;
				await db.SaveChangesAsync(token);
			}
			// Nothing to remove: answer with the current state, write nothing (idempotent retry).
		}, ct);

	/// <summary>
	/// The shared shape of all three: validate, re-validate trust while holding the device row and commit
	/// inside that same transaction, then - strictly after the commit - answer the caller and tell BOTH
	/// parties, each with the copy that is true for IT.
	/// </summary>
	private async Task<Result<PersonalRelationChange>> MutateAsync(
		ClaimsPrincipal actingUser, int subjectLegacyUserId, Func<int, DateTime, CancellationToken, Task> mutate, CancellationToken ct)
	{
		int actorId = actingUser.Id();

		if (subjectLegacyUserId == actorId)
		{
			return Result<PersonalRelationChange>.Failure("You cannot do this to yourself.", HttpStatusCode.BadRequest);
		}

		if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == subjectLegacyUserId, ct))
		{
			return Result<PersonalRelationChange>.Failure("Unknown user.", HttpStatusCode.NotFound);
		}

		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		// Re-validated, locked, immediately before the row actually commits - see TrustedMutation's own
		// doc comment; the broadcast below only ever runs after a successful commit.
		TrustCheckResult trust = await TrustedMutation.RunAsync(
			db, trustChecker, actingUser, () => mutate(actorId, now, ct), ct);

		if (!trust.IsTrusted)
		{
			return Result<PersonalRelationChange>.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		PersonalRelationChange forActor = await DescribeAsync(actorId, subjectLegacyUserId, ct);
		PersonalRelationChange forSubject = await DescribeAsync(subjectLegacyUserId, actorId, ct);

		// Each side gets its OWN copy: the two do not see the same thing (the blocker keeps the subject's
		// profile, the subject reads "unknown"), so this is two payloads rather than one reinterpreted by
		// the client. Equal visibility over SignalR and HTTP: both are built by the same resolver that
		// GET /friends uses.
		await hubContext.Clients.GetUserById(actorId).SendAsync(PersonalRelationChanged, forActor, ct);
		await hubContext.Clients.GetUserById(subjectLegacyUserId).SendAsync(PersonalRelationChanged, forSubject, ct);

		return Result<PersonalRelationChange>.Success(forActor);
	}

	/// <summary>
	/// One side's committed view of the relation. Shared by the HTTP answer, both live copies and
	/// <c>GET /friends</c> (through <see cref="PersonalRelationProjection"/>), so no surface can drift.
	/// </summary>
	private async Task<PersonalRelationChange> DescribeAsync(int viewerId, int otherId, CancellationToken ct)
	{
		var relation = await db.Friendships.AsNoTracking()
			.Where(f => (f.SenderUserId == viewerId && f.TargetUserId == otherId)
				|| (f.SenderUserId == otherId && f.TargetUserId == viewerId))
			.Select(f => new { f.Status, IsInitiator = f.SenderUserId == viewerId })
			.FirstOrDefaultAsync(ct);

		PersonalBlockPair pair = await relations.BlocksBetweenAsync(viewerId, otherId, ct);
		ProfileIdentity profile = await profiles.ResolveAsync(viewerId, otherId, ct);
		IReadOnlySet<int> suspended = await relations.SuspendedCorrespondentsAsync(viewerId, [otherId], ct);

		return PersonalRelationProjection.Project(
			otherId, profile, relation?.Status, relation?.IsInitiator ?? false, pair, suspended.Contains(otherId));
	}
}

/// <summary>
/// The one projection of "legacy friendship row + block overlay" onto what a client actually displays.
/// Used by the relation writer and by <c>GET /friends</c>, so the list and the event agree.
/// </summary>
public static class PersonalRelationProjection
{
	public static PersonalRelationChange Project(
		int otherId, ProfileIdentity profile, FriendshipStatus? legacyStatus, bool legacyIsInitiator,
		PersonalBlockPair blocks, bool correspondenceSuspended)
	{
		// An active block is shown AS the relation status, whichever direction the underlying request
		// went, because a block can also exist with no friendship row at all (blocking someone met in a
		// group). IsInitiator is then forced to "the blocker is not the initiator", which is the legacy
		// convention every existing contact screen already filters by: the blocked-contacts list is
		// `status == Blocked && !isInitiator`, so the blocker - and only the blocker - sees it there and
		// can lift it.
		if (blocks.AnyBlock)
		{
			return new PersonalRelationChange(
				otherId, profile, (int)FriendshipStatus.Blocked, !blocks.ViewerBlockedSubject,
				blocks.ViewerBlockedSubject, CorrespondenceSuspended: true);
		}

		return new PersonalRelationChange(
			otherId,
			profile,
			(int)(legacyStatus ?? FriendshipStatus.Rejected),
			legacyIsInitiator,
			BlockedByMe: false,
			correspondenceSuspended);
	}
}
