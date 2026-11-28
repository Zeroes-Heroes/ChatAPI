using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Sync.Contracts;
using Services.Sync.Interface;
using Services.Sync.Models;
using static Services.Sync.Service.SyncWire;

namespace Services.Sync.Service;

/// <summary>See <see cref="ISyncPageService"/>.</summary>
public sealed class SyncPageService(
	AppDbContext db,
	IChatAccessAuthority authority,
	ISyncCursorStore cursors,
	SyncProjection projection,
	TimeProvider timeProvider,
	ILogger<SyncPageService> logger) : ISyncPageService
{
	private static readonly JsonSerializerOptions SyncJson = SyncProtocol.CreateJsonOptions();

	/// <summary>One unit a page may not split: a message with the statuses reported alongside it, or one journal change.</summary>
	private sealed record PageGroup(long Position, IReadOnlyList<SyncChange> Changes);

	public async Task<SyncResult<SyncPage>> ReadCatalogAsync(
		ClaimsPrincipal caller, SyncPageRequest request, CancellationToken ct = default)
	{
		PageContext context = await OpenAsync(caller, request, SyncCursorSurface.Catalog, ct);
		if (context.Failure is SyncErrorCode failure)
		{
			return SyncResult<SyncPage>.Fail(failure);
		}

		SyncSessionEntity session = context.Session!;
		if (context.Scope is not null)
		{
			// The catalog is the account's own stream and carries no chat scope.
			return SyncResult<SyncPage>.Fail(SyncErrorCode.InvalidRequest);
		}

		// The Full page rebuilds the permitted set from the fixed boundary rows and never reads the
		// journal, so no watermark applies to it. The Delta page walks the very same account journal as
		// the delta surface below and is guarded identically - otherwise a pruned window would be dropped
		// from the page AND reported as applied up to the fixed bound.
		return session.Mode == SyncSessionMode.Full
			? await PageAsync(context, CatalogFullAsync, FullCatalogBounds(session), ct)
			: await PageAsync(context, CatalogDeltaAsync, CatalogDeltaBounds(session), ct, PruneGuardAsync);
	}

	public async Task<SyncResult<SyncPage>> ReadHistoryAsync(
		ClaimsPrincipal caller, SyncPageRequest request, CancellationToken ct = default)
	{
		PageContext context = await OpenAsync(caller, request, SyncCursorSurface.History, ct);
		if (context.Failure is SyncErrorCode failure)
		{
			return SyncResult<SyncPage>.Fail(failure);
		}

		if (context.Scope is null)
		{
			// History is always about one chat scope; the account's chat set is the catalog's answer.
			return SyncResult<SyncPage>.Fail(SyncErrorCode.InvalidRequest);
		}

		// The content enumeration is bounded by the newest message the scope may read, in MESSAGE
		// sequence space - the space its history grants are expressed in.
		long upper = await projection.MessageBoundAsync(context.Scope, ct);
		return await PageAsync(context, HistoryAsync, new PageBounds(First: 1, Upper: upper), ct);
	}

	public async Task<SyncResult<SyncPage>> ReadDeltaAsync(
		ClaimsPrincipal caller, SyncPageRequest request, CancellationToken ct = default)
	{
		PageContext context = await OpenAsync(caller, request, SyncCursorSurface.Delta, ct);
		if (context.Failure is SyncErrorCode failure)
		{
			return SyncResult<SyncPage>.Fail(failure);
		}

		SyncSessionEntity session = context.Session!;

		if (context.Scope is null)
		{
			return await PageAsync(context, CatalogDeltaAsync, CatalogDeltaBounds(session), ct, PruneGuardAsync);
		}

		PageBounds bounds = new(
			First: (context.Boundary!.UploadedSequence ?? 0) + 1,
			Upper: context.Boundary.ThroughChangeSequence);

		return await PageAsync(context, ChatDeltaAsync, bounds, ct, PruneGuardAsync);
	}

	/// <summary>Where an enumeration starts when no cursor names a position yet, and the fixed end it may never pass.</summary>
	private sealed record PageBounds(long First, long Upper);

	private static PageBounds FullCatalogBounds(SyncSessionEntity session) =>
		new(First: 0, Upper: session.BoundaryCount == 0 ? 0 : session.BoundaryCount - 1);

	/// <summary>
	/// A delta continues from what this device told the server it holds (its Resume position); a device
	/// that uploaded nothing starts at the beginning of the journal - and is then refused by the
	/// pruned-journal guard if that beginning is gone.
	/// </summary>
	private static PageBounds CatalogDeltaBounds(SyncSessionEntity session) =>
		new(First: (session.CatalogUploadedSequence ?? 0) + 1, Upper: session.CatalogThroughSequence);

	/// <summary>
	/// "Почистването на журнала запазва възстановимостта": a delta may only start above the pruned
	/// watermark. Below it the server cannot prove the page would skip nothing, so the device is sent to
	/// reconcile its allowed state (the full surfaces, which read the entity tables) instead of being
	/// handed a page with a hole in it.
	/// </summary>
	private async Task<SyncErrorCode?> PruneGuardAsync(PageContext context, long from, CancellationToken ct)
	{
		long applied = from - 1;
		long pruned = context.Scope is null
			? await db.SyncAccountCatalogCounters.AsNoTracking()
				.Where(c => c.AccountId == context.Session!.AccountId)
				.Select(c => c.PrunedThroughCatalogSequence)
				.FirstOrDefaultAsync(ct)
			: await db.SyncChatSequenceCounters.AsNoTracking()
				.Where(c => c.ChatId == context.Scope.ChatId)
				.Select(c => c.PrunedThroughChangeSequence)
				.FirstOrDefaultAsync(ct);

		return applied < pruned ? SyncErrorCode.CursorInvalid : null;
	}

	/// <summary>
	/// The shared page machine: resolve or mint the cursor, load the groups of the range, fit them into
	/// both caps, memoize exactly what was answered and mint the continuation.
	/// </summary>
	private async Task<SyncResult<SyncPage>> PageAsync(
		PageContext context,
		Func<PageContext, long, long, int, CancellationToken, Task<IReadOnlyList<PageGroup>>> load,
		PageBounds bounds,
		CancellationToken ct,
		Func<PageContext, long, CancellationToken, Task<SyncErrorCode?>>? guard = null)
	{
		SyncSessionEntity session = context.Session!;
		DateTime now = context.Now;
		SyncCursorEntity cursor;

		if (context.Request.Cursor is null)
		{
			cursor = cursors.Mint(
				session, context.Surface, context.Scope is null ? SyncStreamKind.Catalog : SyncStreamKind.Chat,
				context.Scope?.ChatId, context.Scope?.MembershipId, bounds.First, bounds.Upper, now);
			await db.SaveChangesAsync(ct);
		}
		else
		{
			SyncCursorEntity? resolved = await cursors.ResolveAsync(context.Request.Cursor, now, ct);
			if (resolved is null
				|| !SyncSessionGate.Binds(resolved, session, context.Surface, context.Scope?.ChatId, context.Scope?.MembershipId)
				|| resolved.UpperBoundSequence != bounds.Upper)
			{
				// Unknown, expired, foreign, for another surface, or minted against a different fixed
				// bound: "Invalid/expired/mismatched cursor returns CursorInvalid and starts allowed-state
				// reconciliation preserving pending send/Seen queues."
				return SyncResult<SyncPage>.Fail(SyncErrorCode.CursorInvalid);
			}

			cursor = resolved;
		}

		// Applied to the position this page would actually start at, not to the enumeration's nominal
		// first: a continuation that is already above the watermark stays valid even if the journal was
		// cleaned under it, and one that is not is refused rather than answered with a hole.
		if (guard is not null && await guard(context, cursor.FromSequence, ct) is SyncErrorCode guarded)
		{
			return SyncResult<SyncPage>.Fail(guarded);
		}

		// A limit of 0 means "whatever the server's own cap is"; anything outside 0..100 was already
		// refused as a malformed request.
		int limit = context.Request.Limit > 0
			? Math.Min(context.Request.Limit, SyncProtocol.MaxPageItems)
			: SyncProtocol.MaxPageItems;

		if (cursor.IssuedThroughSequence is long issued)
		{
			// A retried cursor answers the page it already answered: the same range, the same
			// continuation, the same terminal flag.
			IReadOnlyList<PageGroup> replayGroups = await load(context, cursor.FromSequence, issued, SyncProtocol.MaxPageItems, ct);
			List<SyncChange> replayChanges = [.. replayGroups.SelectMany(g => g.Changes)];
			if (cursor.IsTerminal)
			{
				replayChanges.AddRange(await TerminalChangesAsync(context, ct));
			}

			return SyncResult<SyncPage>.Ok(Page(context, replayChanges, issued, cursor.IsTerminal, cursor.NextCursorId));
		}

		IReadOnlyList<PageGroup> groups = await load(context, cursor.FromSequence, cursor.UpperBoundSequence, limit, ct);

		List<PageGroup> fitted = [];
		int items = 0;
		foreach (PageGroup group in groups)
		{
			// "Pages are bounded by both 100 items and 1048576 serialized UTF-8 bytes" - and a group is
			// never split, so the first one is always taken even if it is large on its own.
			if (fitted.Count > 0 && items + group.Changes.Count > SyncProtocol.MaxPageItems)
			{
				break;
			}

			fitted.Add(group);
			items += group.Changes.Count;
		}

		long through = fitted.Count > 0 ? fitted[^1].Position : cursor.UpperBoundSequence;
		bool complete = through >= cursor.UpperBoundSequence;

		List<SyncChange> changes = [.. fitted.SelectMany(g => g.Changes)];
		if (complete)
		{
			changes.AddRange(await TerminalChangesAsync(context, ct));
		}

		// The byte cap is enforced on the real serialized page, including keys and envelopes, by dropping
		// whole groups until it fits.
		while (Oversized(context, changes, through, complete))
		{
			if (fitted.Count <= 1)
			{
				// A single group that cannot fit the cap fails explicitly rather than producing an
				// unbounded response - the contract's own rule for an individual entity over the cap.
				logger.LogError(
					"Sync page for session {SyncSessionId} cannot fit one change group inside the page cap.", session.Id);
				return SyncResult<SyncPage>.Fail(SyncErrorCode.Unavailable);
			}

			fitted.RemoveAt(fitted.Count - 1);
			through = fitted[^1].Position;
			complete = through >= cursor.UpperBoundSequence;
			changes = [.. fitted.SelectMany(g => g.Changes)];
			if (complete)
			{
				changes.AddRange(await TerminalChangesAsync(context, ct));
			}
		}

		string? nextToken = null;
		cursor.IssuedThroughSequence = through;
		cursor.IsTerminal = complete;
		if (!complete)
		{
			SyncCursorEntity next = cursors.Mint(
				session, context.Surface, cursor.StreamKind, cursor.ChatId, cursor.MembershipId,
				through + 1, cursor.UpperBoundSequence, now);
			cursor.NextCursorId = next.Id;
			nextToken = next.Id;
		}

		await db.SaveChangesAsync(ct);

		return SyncResult<SyncPage>.Ok(Page(context, changes, through, complete, nextToken));
	}

	private bool Oversized(PageContext context, List<SyncChange> changes, long through, bool complete) =>
		JsonSerializer.SerializeToUtf8Bytes(Page(context, changes, through, complete, new string('A', 43)), SyncJson).Length
			> SyncProtocol.MaxPageBytes;

	private static SyncPage Page(PageContext context, List<SyncChange> changes, long through, bool complete, string? nextCursor) =>
		new(SyncProtocol.Version,
			context.Session!.Id,
			context.Request.Boundary,
			changes,
			nextCursor,
			AppliedThrough(context, through, complete),
			complete);

	/// <summary>
	/// What this page lets the device claim it has applied. A JOURNAL page advances position by position,
	/// so it reports what it just covered. A CONTENT enumeration (the full catalog, the history surface)
	/// does not walk the change stream at all: it rebuilds the current permitted state, so it claims the
	/// stream's fixed boundary only on its terminal page and zero before that - a half-restored scope
	/// must never look like a stream that has been applied up to some middle position.
	/// </summary>
	private static StreamPosition AppliedThrough(PageContext context, long through, bool complete)
	{
		bool journal = context.Surface == SyncCursorSurface.Delta
			|| (context.Surface == SyncCursorSurface.Catalog && context.Session!.Mode == SyncSessionMode.Delta);

		if (context.Scope is SyncScopeContext scope)
		{
			return ChatPosition(scope.ChatId, scope.MembershipId, journal ? through : (complete ? scope.ThroughChangeSequence : 0));
		}

		return CatalogPosition(journal ? through : (complete ? context.Session!.CatalogThroughSequence : 0));
	}

	/// <summary>
	/// What a completed CONTENT enumeration of a chat scope owes beyond its messages: the stint's own
	/// unread counter, its membership snapshot with the ranges it may read, and the durable membership
	/// events that belong to its history. A journal page delivers each of these at its own position
	/// instead, so it adds nothing here.
	/// </summary>
	private async Task<IReadOnlyList<SyncChange>> TerminalChangesAsync(PageContext context, CancellationToken ct)
	{
		if (context.Surface != SyncCursorSurface.History || context.Scope is not SyncScopeContext scope)
		{
			return [];
		}

		List<SyncChange> changes = [];
		long position = scope.ThroughChangeSequence;

		MembershipSnapshot? membership = await projection.MembershipSnapshotAsync(scope, ct);
		if (membership is not null)
		{
			changes.Add(SyncChangeFactory.Membership(scope, membership, position));
		}

		changes.Add(SyncChangeFactory.Unread(scope, await projection.UnreadAsync(scope, ct), position));

		foreach ((string membershipId, MembershipEvent change) in
			await projection.RecoverableMembershipEventsAsync(scope, context.Caller!.AccountContextId, ct))
		{
			changes.Add(SyncChangeFactory.MembershipEventChange(scope, membershipId, change, position));
		}

		return changes;
	}

	/// <summary>Full-mode catalog: the session's own fixed boundary set, as chat snapshots, paged by ordinal.</summary>
	private async Task<IReadOnlyList<PageGroup>> CatalogFullAsync(
		PageContext context, long from, long through, int limit, CancellationToken ct)
	{
		SyncSessionEntity session = context.Session!;

		List<SyncSessionBoundaryEntity> rows = await db.SyncSessionBoundaries.AsNoTracking()
			.Where(b => b.SyncSessionId == session.Id && b.Ordinal >= from && b.Ordinal <= through)
			.OrderBy(b => b.Ordinal)
			.Take(limit)
			.ToListAsync(ct);

		List<PageGroup> groups = [];
		foreach (SyncSessionBoundaryEntity row in rows)
		{
			SyncScopeContext scope = SyncScopeContext.Of(row, session.SnapshotId);
			ChatSnapshot? snapshot = await projection.ChatSnapshotAsync(scope, ct);
			if (snapshot is null)
			{
				// An isolated unreadable record is diagnosed and left out, but its ORDINAL still advances -
				// skipping the position itself would end the enumeration early and lose every chat after it.
				logger.LogWarning("Sync catalog skipped chat {ChatId}: no readable projection for stint {MembershipId}.", row.ChatId, row.MembershipId);
			}

			groups.Add(new PageGroup(
				row.Ordinal,
				snapshot is null ? [] : [SyncChangeFactory.Chat(scope, snapshot, CatalogPosition(session.CatalogThroughSequence))]));
		}

		return groups;
	}

	/// <summary>Delta catalog: the account's catalog journal, each entry resolved to the chat's current permitted snapshot.</summary>
	private async Task<IReadOnlyList<PageGroup>> CatalogDeltaAsync(
		PageContext context, long from, long through, int limit, CancellationToken ct)
	{
		SyncSessionEntity session = context.Session!;

		var entries = await db.SyncCatalogEntries.AsNoTracking()
			.Where(e => e.AccountId == session.AccountId && e.CatalogSequence >= from && e.CatalogSequence <= through)
			.OrderBy(e => e.CatalogSequence)
			.Take(limit)
			.Select(e => new { e.CatalogSequence, e.ChatId, e.ReferenceId })
			.ToListAsync(ct);

		if (entries.Count == 0)
		{
			return [];
		}

		Dictionary<string, SyncSessionBoundaryEntity> boundaries = await db.SyncSessionBoundaries.AsNoTracking()
			.Where(b => b.SyncSessionId == session.Id)
			.ToDictionaryAsync(b => b.MembershipId, StringComparer.Ordinal, ct);

		List<PageGroup> groups = [];
		foreach (var entry in entries)
		{
			// Every catalog entry names the stint it is about. One outside this session's fixed set is a
			// scope the handshake did not fix - it belongs to a later rights version, not to this view -
			// so the position advances and nothing is delivered for it.
			if (entry.ReferenceId is null
				|| !boundaries.TryGetValue(entry.ReferenceId, out SyncSessionBoundaryEntity? boundary)
				|| !string.Equals(boundary.ChatId, entry.ChatId, StringComparison.Ordinal))
			{
				groups.Add(new PageGroup(entry.CatalogSequence, []));
				continue;
			}

			SyncScopeContext scope = SyncScopeContext.Of(boundary, session.SnapshotId);
			ChatSnapshot? snapshot = await projection.ChatSnapshotAsync(scope, ct);

			groups.Add(new PageGroup(
				entry.CatalogSequence,
				snapshot is null ? [] : [SyncChangeFactory.Chat(scope, snapshot, CatalogPosition(entry.CatalogSequence))]));
		}

		return groups;
	}

	/// <summary>Full content of one chat scope: whole messages with their keys, author keys and statuses, in message-sequence order.</summary>
	private async Task<IReadOnlyList<PageGroup>> HistoryAsync(
		PageContext context, long from, long through, int limit, CancellationToken ct)
	{
		SyncScopeContext scope = context.Scope!;

		List<SyncMessageEntity> messages = await projection.PermittedMessagesAsync(
			scope, from, through, limit, newestFirst: false, ct);

		IReadOnlyList<SyncProjection.MessageGroup> described = await projection.DescribeMessagesAsync(
			scope, messages, context.Caller!, includeStatuses: true, ct);

		// A message whose key or author key is unusable is left out of its own group but still advances
		// the enumeration, so one broken record cannot block the rest of the permitted set.
		Dictionary<long, SyncProjection.MessageGroup> bySequence = described.ToDictionary(g => g.MessageSequence);

		return
		[
			.. messages.Select(message => new PageGroup(
				message.MessageSequence,
				bySequence.TryGetValue(message.MessageSequence, out SyncProjection.MessageGroup? group) ? group.Changes : [])),
		];
	}

	/// <summary>Delta of one chat stream: the journal, each pointer resolved into the payload the caller may see.</summary>
	private async Task<IReadOnlyList<PageGroup>> ChatDeltaAsync(
		PageContext context, long from, long through, int limit, CancellationToken ct)
	{
		SyncScopeContext scope = context.Scope!;

		List<SyncChatChangeEntity> rows = await db.SyncChatChanges.AsNoTracking()
			.Where(c => c.ChatId == scope.ChatId && c.ChangeSequence >= from && c.ChangeSequence <= through)
			.OrderBy(c => c.ChangeSequence)
			.Take(limit)
			.ToListAsync(ct);

		List<PageGroup> groups = [];
		foreach (SyncChatChangeEntity row in rows)
		{
			SyncChange? change = await ResolveAsync(context, scope, row, ct);

			// A change this caller may not see leaves an EMPTY group: the position still advances, so the
			// page neither skips a position nor reveals anything.
			groups.Add(new PageGroup(row.ChangeSequence, change is null ? [] : [change]));
		}

		return groups;
	}

	/// <summary>One journal pointer, resolved against committed state and filtered by what this scope may see.</summary>
	private async Task<SyncChange?> ResolveAsync(
		PageContext context, SyncScopeContext scope, SyncChatChangeEntity row, CancellationToken ct)
	{
		switch (row.Kind)
		{
			case SyncChangeKind.Message:
			{
				SyncMessageEntity? message = await PermittedMessageAsync(scope, row.ReferenceId, ct);
				if (message is null)
				{
					return null;
				}

				IReadOnlyList<SyncProjection.MessageGroup> described = await projection.DescribeMessagesAsync(
					scope, [message], context.Caller!, includeStatuses: false, ct);

				return described.Count == 0 ? null : described[0].Changes[0];
			}

			case SyncChangeKind.RecipientState:
			{
				SyncMessageEntity? message = await PermittedMessageAsync(scope, row.ReferenceId, ct);
				if (message is null || row.SecondaryId is null)
				{
					return null;
				}

				MessageRecipientState? state = await projection.RecipientStateAsync(scope, message.Id, row.SecondaryId, ct);
				return state is null ? null : SyncChangeFactory.RecipientState(scope, state, row.ChangeSequence);
			}

			case SyncChangeKind.DeviceState:
			{
				// The journal entry names the SQLite copy that reported it (SecondaryId), and the ONLY
				// surface it means anything to is that copy. Without this filter another local instance of
				// the same account would be handed a change describing ITS OWN row at a position where a
				// different copy's row moved - not a leak (it is the caller's own data, and the EventId is
				// derived from the caller's own version, so it dedupes) but a spurious change that says
				// something happened to a row that did not change.
				if (!string.Equals(row.SecondaryId, context.Caller!.LocalInstanceId, StringComparison.Ordinal))
				{
					return null;
				}

				SyncMessageEntity? message = await PermittedMessageAsync(scope, row.ReferenceId, ct);
				if (message is null)
				{
					return null;
				}

				// Device progress is private to the device that reported it; another device of the same
				// person learns nothing from it (the person's own public state is the recipient state).
				MessageDeviceState? state = await projection.OwnDeviceStateAsync(context.Caller!, message.Id, ct);
				return state is null ? null : SyncChangeFactory.DeviceState(scope, state, row.ChangeSequence);
			}

			case SyncChangeKind.Unread:
			{
				// Unread is per stint and per person: only this scope's own counter is this caller's.
				return string.Equals(row.ReferenceId, scope.MembershipId, StringComparison.Ordinal)
					? SyncChangeFactory.Unread(scope, await projection.UnreadAsync(scope, ct), row.ChangeSequence)
					: null;
			}

			case SyncChangeKind.Membership:
			{
				if (!string.Equals(row.ReferenceId, scope.MembershipId, StringComparison.Ordinal))
				{
					return null;
				}

				MembershipSnapshot? snapshot = await projection.MembershipSnapshotAsync(scope, ct);
				return snapshot is null ? null : SyncChangeFactory.Membership(scope, snapshot, row.ChangeSequence);
			}

			case SyncChangeKind.MembershipEvent:
			{
				if (row.MembershipEventKind is not SyncMembershipEventKind kind)
				{
					return null;
				}

				MembershipEvent? change = await projection.MembershipEventAsync(
					scope, row.ReferenceId, kind, row.OccurredAtUtc, context.Caller!.AccountContextId, ct);

				return change is null ? null : SyncChangeFactory.MembershipEventChange(scope, row.ReferenceId, change, row.ChangeSequence);
			}

			case SyncChangeKind.Chat:
			default:
			{
				ChatSnapshot? snapshot = await projection.ChatSnapshotAsync(scope, ct);
				return snapshot is null
					? null
					: SyncChangeFactory.Chat(scope, snapshot, ChatPosition(scope.ChatId, scope.MembershipId, row.ChangeSequence));
			}
		}
	}

	/// <summary>A message only if this scope's own history grants really cover it - the one authority for v1 content.</summary>
	private async Task<SyncMessageEntity?> PermittedMessageAsync(SyncScopeContext scope, string messageId, CancellationToken ct)
	{
		SyncMessageEntity? message = await db.SyncMessages.AsNoTracking()
			.FirstOrDefaultAsync(m => m.Id == messageId && m.ChatId == scope.ChatId, ct);

		if (message is null || message.ChangeSequence > scope.ThroughChangeSequence)
		{
			return null;
		}

		long bound = await projection.MessageBoundAsync(scope, ct);
		List<(long From, long Through, long? FrozenAt)> grants = await projection.GrantsAsync(scope.MembershipId, bound, ct);

		return grants.Any(g => message.MessageSequence >= g.From && message.MessageSequence <= g.Through) ? message : null;
	}

	/// <summary>The resolved request: the caller, the session, the scope the boundary names and the cursor surface it belongs to.</summary>
	private sealed record PageContext(
		SyncPageRequest Request,
		SyncCursorSurface Surface,
		DateTime Now,
		SyncSessionEntity? Session,
		SyncCallerIdentity? Caller,
		SyncScopeContext? Scope,
		SyncSessionBoundaryEntity? Boundary,
		SyncErrorCode? Failure);

	/// <summary>
	/// Everything a page must establish before it reads anything: the protocol version, the caller's own
	/// identity, the session, the CURRENT rights, that the supplied boundary really is one this session
	/// fixed, and - for a chat scope - the shared access check every other surface asks.
	/// </summary>
	private async Task<PageContext> OpenAsync(
		ClaimsPrincipal caller, SyncPageRequest request, SyncCursorSurface surface, CancellationToken ct)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		if (request?.Boundary?.Through is null)
		{
			return Refuse(request, surface, now, SyncErrorCode.InvalidRequest);
		}

		if (request.ProtocolVersion != SyncProtocol.Version)
		{
			return Refuse(request, surface, now, SyncErrorCode.UnsupportedVersion);
		}

		if (request.Limit < 0 || request.Limit > SyncProtocol.MaxPageItems || !IsWellFormed(request.Boundary.Through))
		{
			return Refuse(request, surface, now, SyncErrorCode.InvalidRequest);
		}

		SyncSessionGate.Context gate = await SyncSessionGate.OpenAsync(db, caller, request.SyncSessionId, now, ct);
		if (gate.Failure is SyncErrorCode failure)
		{
			return Refuse(request, surface, now, failure);
		}

		SyncSessionEntity session = gate.Session!;
		if (!string.Equals(request.Boundary.SnapshotId, session.SnapshotId, StringComparison.Ordinal))
		{
			// A boundary from another session's snapshot describes another fixed view entirely.
			return Refuse(request, surface, now, SyncErrorCode.InvalidRequest);
		}

		if (request.Boundary.Scope is null)
		{
			bool catalogBoundary = request.Boundary.Through.StreamKind == StreamKind.Catalog
				&& string.Equals(request.Boundary.Through.Sequence, Seq(session.CatalogThroughSequence), StringComparison.Ordinal);

			return catalogBoundary
				? new PageContext(request, surface, now, session, gate.Caller, null, null, null)
				: Refuse(request, surface, now, SyncErrorCode.InvalidRequest);
		}

		MembershipScope scope = request.Boundary.Scope;
		SyncSessionBoundaryEntity? boundary = await db.SyncSessionBoundaries.AsNoTracking()
			.FirstOrDefaultAsync(b => b.SyncSessionId == session.Id
				&& b.ChatId == scope.ChatId
				&& b.MembershipId == scope.MembershipId, ct);

		if (boundary is null
			|| !string.Equals(scope.RightsVersion, Seq(boundary.RightsVersion), StringComparison.Ordinal)
			|| request.Boundary.Through.StreamKind != StreamKind.Chat
			|| !string.Equals(request.Boundary.Through.ChatId, boundary.ChatId, StringComparison.Ordinal)
			|| !string.Equals(request.Boundary.Through.MembershipId, boundary.MembershipId, StringComparison.Ordinal)
			|| !string.Equals(request.Boundary.Through.Sequence, Seq(boundary.ThroughChangeSequence), StringComparison.Ordinal))
		{
			// The boundary must be exactly one this handshake fixed: a scope, a rights version or a bound
			// the client invented is not a view this session has.
			return Refuse(request, surface, now, SyncErrorCode.InvalidRequest);
		}

		if (!int.TryParse(boundary.ChatId, NumberStyles.None, CultureInfo.InvariantCulture, out int legacyChatId))
		{
			return Refuse(request, surface, now, SyncErrorCode.InvalidRequest);
		}

		// The same shared check HTTP, socket and deferred delivery already share - including the stage-06
		// fresh-start exclusion. The stint's own sequence-space grants then decide each message; this
		// decides whether the chat may be read at all right now.
		ChatAccess access = await authority.GetAsync(caller, legacyChatId, ct);
		if (!access.CanRead)
		{
			// The rights version has not moved, yet the shared check refuses the chat: fail closed and
			// make the device reconcile rather than serve a page from a view nobody vouches for.
			return Refuse(request, surface, now, SyncErrorCode.RightsChanged);
		}

		return new PageContext(
			request, surface, now, session, gate.Caller, SyncScopeContext.Of(boundary, session.SnapshotId), boundary, null);
	}

	private static PageContext Refuse(SyncPageRequest? request, SyncCursorSurface surface, DateTime now, SyncErrorCode code) =>
		new(request!, surface, now, null, null, null, null, code);
}
