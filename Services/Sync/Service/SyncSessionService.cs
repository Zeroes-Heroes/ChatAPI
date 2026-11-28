using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Sync.Contracts;
using Services.Sync.Interface;
using Services.Sync.Models;
using static Services.Sync.Service.SyncWire;

namespace Services.Sync.Service;

/// <summary>See <see cref="ISyncSessionService"/>.</summary>
public sealed class SyncSessionService(
	AppDbContext db,
	ISessionTrustChecker trustChecker,
	ISyncCursorStore cursors,
	TimeProvider timeProvider) : ISyncSessionService
{
	/// <summary>
	/// How long a fixed boundary set (and therefore every cursor minted from it) stays usable. Long
	/// enough for a full restoration over a slow link, short enough that an abandoned session's rows do
	/// not accumulate; expiry costs a new handshake and never an applied position.
	/// </summary>
	private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

	private static readonly JsonSerializerOptions SyncJson = SyncProtocol.CreateJsonOptions();

	public async Task<SyncResult<SyncHandshakeResponse>> HandshakeAsync(
		ClaimsPrincipal caller, string connectionId, SyncHandshakeRequest request, CancellationToken ct = default)
	{
		if (request?.Session is null || string.IsNullOrEmpty(connectionId))
		{
			return SyncResult<SyncHandshakeResponse>.Fail(SyncErrorCode.InvalidRequest);
		}

		if (request.ProtocolVersion != SyncProtocol.Version)
		{
			// "Unknown protocolVersion fails before mutation."
			return SyncResult<SyncHandshakeResponse>.Fail(SyncErrorCode.UnsupportedVersion);
		}

		SyncCallerIdentity? identity = await SyncCallerIdentity.ResolveAsync(db, caller, ct);
		if (identity is null)
		{
			return SyncResult<SyncHandshakeResponse>.Fail(SyncErrorCode.SessionExpired);
		}

		// The supplied context is CHECKED, never used in place of the authenticated one.
		if (!identity.Matches(request.Session))
		{
			return SyncResult<SyncHandshakeResponse>.Fail(SyncErrorCode.InvalidRequest);
		}

		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		SyncHandshakeResponse? response = null;
		SyncErrorCode? failure = null;

		// The device/session lock, for the same reason every other write takes it: a handshake admitted a
		// moment before a concurrent revoke's commit must not go on to fix a boundary set for a device
		// that is no longer trusted.
		TrustCheckResult trust = await TrustedMutation.RunAsync(db, trustChecker, caller, async () =>
		{
			// Takes the account's catalog counter row for the whole transaction. Every membership grant or
			// freeze for this account allocates its catalog sequence from exactly this row, so while it is
			// held no rights change of this account can commit - which is what makes the captured set, the
			// catalog boundary and the rights version ONE consistent snapshot instead of three reads that
			// could straddle a membership mutation. Taken after the account/device/session locks and
			// before nothing else at all - the membership module reaches this same row last, after the
			// chat row this transaction never takes - so the two can never deadlock against each other.
			List<SyncAccountCatalogCounterEntity> counters = await db.SyncAccountCatalogCounters
				.FromSqlInterpolated($"""SELECT * FROM "SyncAccountCatalogCounters" WHERE "AccountId" = {identity.AccountContextId} FOR UPDATE""")
				.AsNoTracking()
				.ToListAsync(ct);

			if (counters.Count != 1)
			{
				// Every account context is created with its counter; without one there is no catalog to fix.
				failure = SyncErrorCode.Unavailable;
				return;
			}

			long catalogThrough = counters[0].NextCatalogSequence - 1;

			SyncSessionEntity? existing = await db.SyncSessions
				.FirstOrDefaultAsync(s => s.DeviceSessionId == identity.DeviceSessionId
					&& s.ConnectionId == connectionId
					&& s.EndedAtUtc == null
					&& s.ExpiresAtUtc > now
					&& s.RightsVersion == catalogThrough, ct);

			// A retried handshake (lost answer, duplicate call) on the same connection with unchanged
			// rights continues the session that is already fixed: a second boundary set for one connection
			// would give the device two disagreeing views of its own progress.
			SyncSessionEntity session = existing ?? await FixSessionAsync(identity, connectionId, catalogThrough, now, ct);

			await db.SaveChangesAsync(ct);
			response = await DescribeAsync(session, now, ct);
			await db.SaveChangesAsync(ct);
		}, ct);

		if (!trust.IsTrusted)
		{
			// Uniform and uninformative, like every other fail-closed answer in this codebase.
			return SyncResult<SyncHandshakeResponse>.Fail(SyncErrorCode.SessionExpired);
		}

		return failure is SyncErrorCode code
			? SyncResult<SyncHandshakeResponse>.Fail(code)
			: response is null
				? SyncResult<SyncHandshakeResponse>.Fail(SyncErrorCode.Unavailable)
				: SyncResult<SyncHandshakeResponse>.Ok(response);
	}

	/// <summary>
	/// Captures one session row and its whole fixed boundary set, inside the caller's transaction and
	/// under the catalog counter row it already holds.
	/// </summary>
	private async Task<SyncSessionEntity> FixSessionAsync(
		SyncCallerIdentity identity, string connectionId, long catalogThrough, DateTime now, CancellationToken ct)
	{
		SyncLocalInstanceEntity instance = await db.SyncLocalInstances
			.FirstAsync(i => i.Id == identity.LocalInstanceId, ct);

		// "Full mode persists until the account/local-instance initial restore marker completes,
		// including after restart" - the marker is a column on the local instance, so a restart in the
		// middle of the first restoration keeps the whole initial barrier.
		SyncSessionMode mode = instance.InitialRestoreCompletedAtUtc is null ? SyncSessionMode.Full : SyncSessionMode.Delta;

		SyncSessionEntity session = new(
			Guid.NewGuid().ToString("D"),
			identity.AccountContextId,
			identity.DeviceRegistrationId,
			identity.LocalInstanceId,
			identity.DeviceSessionId,
			connectionId,
			mode,
			// The account's catalog position IS its rights version: every membership grant and every
			// freeze appends a catalog entry for the account, so this number moves exactly when access
			// does - including when old history is newly shared, which is what makes such a grant
			// invalidate this session instead of waiting for a cursor to reach it.
			catalogThrough,
			catalogThrough,
			Guid.NewGuid().ToString("D"),
			now,
			now.Add(SessionLifetime));

		db.SyncSessions.Add(session);

		IReadOnlyList<CapturedBoundary> captured = await CaptureBoundariesAsync(identity.AccountContextId, ct);
		int ordinal = 0;
		foreach (CapturedBoundary boundary in captured)
		{
			db.SyncSessionBoundaries.Add(new SyncSessionBoundaryEntity(
				session.Id, ordinal++, boundary.ChatId, boundary.MembershipId, boundary.RightsVersion,
				boundary.ThroughChangeSequence, boundary.IsFrozen)
			{
				PriorityRank = boundary.PriorityRank,
			});
		}

		session.BoundaryCount = captured.Count;
		return session;
	}

	/// <summary>One scope of the fixed set, already in enumeration order.</summary>
	private sealed record CapturedBoundary(
		string ChatId, string MembershipId, long RightsVersion, long ThroughChangeSequence, bool IsFrozen, int? PriorityRank);

	/// <summary>
	/// Every stint this account holds - active and frozen - with its own fixed upper bound, ordered
	/// priority set first. A frozen stint's bound is the position it was frozen at: an archive cannot
	/// grow, so later changes in that chat are outside it by construction.
	/// </summary>
	private async Task<IReadOnlyList<CapturedBoundary>> CaptureBoundariesAsync(string accountId, CancellationToken ct)
	{
		var stints = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.AccountId == accountId)
			.Select(m => new
			{
				m.Id,
				m.ChatId,
				m.State,
				m.RightsVersion,
				m.CreatedAtUtc,
			})
			.ToListAsync(ct);

		if (stints.Count == 0)
		{
			return [];
		}

		// Fresh start (plan/06): a chat this context is excluded from is not its history at all. The
		// projection already refuses to create such a stint; this keeps the catalog identical to every
		// other surface's answer even for a stint that predates the exclusion.
		HashSet<string> excluded = [.. (await db.SyncLegacyContextExclusions.AsNoTracking()
			.Where(e => e.AccountId == accountId)
			.Select(e => e.LegacyChatId)
			.ToListAsync(ct))
			.Select(id => id.ToString(CultureInfo.InvariantCulture))];

		string[] chatIds = [.. stints.Select(s => s.ChatId).Where(id => !excluded.Contains(id)).Distinct()];
		if (chatIds.Length == 0)
		{
			return [];
		}

		Dictionary<string, long> chatThrough = await db.SyncChatSequenceCounters.AsNoTracking()
			.Where(c => chatIds.Contains(c.ChatId))
			.ToDictionaryAsync(c => c.ChatId, c => c.NextChangeSequence - 1, StringComparer.Ordinal, ct);

		string[] membershipIds = [.. stints.Select(s => s.Id)];

		Dictionary<string, long> frozenAt = await db.SyncMembershipMetadataSnapshots.AsNoTracking()
			.Where(s => membershipIds.Contains(s.MembershipId))
			.ToDictionaryAsync(s => s.MembershipId, s => s.CapturedAtChangeSequence, StringComparer.Ordinal, ct);

		Dictionary<string, DateTime> lastActivity = await LastActivityAsync(chatIds, ct);

		IReadOnlyList<string> priority = await SelectPriorityChatsAsync(accountId, chatIds, lastActivity, ct);
		Dictionary<string, int> priorityRank = priority
			.Select((chatId, index) => (chatId, rank: index + 1))
			.ToDictionary(x => x.chatId, x => x.rank, StringComparer.Ordinal);

		return
		[
			.. stints
				.Where(s => !excluded.Contains(s.ChatId) && chatThrough.ContainsKey(s.ChatId))
				.Select(s => new
				{
					Stint = s,
					Rank = priorityRank.TryGetValue(s.ChatId, out int rank) ? rank : int.MaxValue,
					Activity = lastActivity.TryGetValue(s.ChatId, out DateTime at) ? at : s.CreatedAtUtc,
				})
				// Priority chats first, in priority order; the rest by their own most recent activity,
				// which is the "наборът се допълва с последните активни чатове" order. A chat's stints stay
				// adjacent (same chat key), oldest stint first, so an archive and its successor are
				// enumerated together.
				.OrderBy(x => x.Rank)
				.ThenByDescending(x => x.Activity)
				.ThenBy(x => x.Stint.ChatId, StringComparer.Ordinal)
				.ThenBy(x => x.Stint.CreatedAtUtc)
				.ThenBy(x => x.Stint.Id, StringComparer.Ordinal)
				.Select(x => new CapturedBoundary(
					x.Stint.ChatId,
					x.Stint.Id,
					x.Stint.RightsVersion,
					x.Stint.State == SyncMembershipState.Frozen && frozenAt.TryGetValue(x.Stint.Id, out long at)
						? at
						: chatThrough[x.Stint.ChatId],
					x.Stint.State == SyncMembershipState.Frozen,
					x.Rank == int.MaxValue ? null : x.Rank)),
		];
	}

	/// <summary>
	/// The up-to-ten priority chats: "Приоритетът се определя от най-скорошното входящо логически
	/// непрочетено съобщение за потребителя в сървърното състояние... Сравнението е по CreatedAtUtc, с
	/// MessageId при равенство. Когато тези чатове са под 10, наборът се допълва с последните активни без
	/// повторения." Only the CURRENT (active) stint's counter takes part, so unread left behind in a
	/// frozen archive after a rejoin never drives the priority.
	/// </summary>
	private async Task<IReadOnlyList<string>> SelectPriorityChatsAsync(
		string accountId, string[] chatIds, Dictionary<string, DateTime> activity, CancellationToken ct)
	{
		List<string> priority = await db.SyncUnreadStates.AsNoTracking()
			.Where(u => chatIds.Contains(u.ChatId)
				&& u.Membership.AccountId == accountId
				&& u.Membership.State == SyncMembershipState.Active
				&& u.Count > 0
				&& u.LatestUnreadCreatedAtUtc != null)
			.OrderByDescending(u => u.LatestUnreadCreatedAtUtc)
			// The contract's tie-breaker, in both message spaces: a chat holds its newest unread in one of
			// them, so exactly one of these two is set on any row that has a pointer at all.
			.ThenByDescending(u => u.LatestUnreadMessageId)
			.ThenByDescending(u => u.LatestUnreadLegacyMessageId)
			.Take(SyncProtocol.MaxPriorityChats)
			.Select(u => u.ChatId)
			.ToListAsync(ct);

		if (priority.Count >= SyncProtocol.MaxPriorityChats)
		{
			return priority;
		}

		HashSet<string> chosen = [.. priority];

		List<string> activeChats = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.AccountId == accountId && m.State == SyncMembershipState.Active && chatIds.Contains(m.ChatId))
			.Select(m => m.ChatId)
			.Distinct()
			.ToListAsync(ct);

		priority.AddRange(activeChats
			.Where(id => !chosen.Contains(id))
			.OrderByDescending(id => activity.TryGetValue(id, out DateTime at) ? at : DateTime.MinValue)
			.ThenBy(id => id, StringComparer.Ordinal)
			.Take(SyncProtocol.MaxPriorityChats - priority.Count));

		return priority;
	}

	/// <summary>
	/// The most recent message instant per chat, over BOTH message spaces: a chat may hold legacy rows
	/// written before stage 11 and sequence-space rows written after it, and "latest active" is one fact
	/// about the chat, not two.
	/// </summary>
	private async Task<Dictionary<string, DateTime>> LastActivityAsync(string[] chatIds, CancellationToken ct)
	{
		Dictionary<string, DateTime> activity = new(StringComparer.Ordinal);

		var v1 = await db.SyncMessages.AsNoTracking()
			.Where(m => chatIds.Contains(m.ChatId))
			.GroupBy(m => m.ChatId)
			.Select(g => new { ChatId = g.Key, Last = g.Max(m => m.CreatedAtUtc) })
			.ToListAsync(ct);

		foreach (var row in v1)
		{
			activity[row.ChatId] = row.Last;
		}

		int[] legacyIds = [.. chatIds
			.Select(id => int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int legacy) ? legacy : -1)
			.Where(id => id > 0)];

		if (legacyIds.Length == 0)
		{
			return activity;
		}

		var legacy = await db.Messages.AsNoTracking()
			.Where(m => legacyIds.Contains(m.ChatId))
			.GroupBy(m => m.ChatId)
			.Select(g => new { ChatId = g.Key, Last = g.Max(m => m.CreatedAt) })
			.ToListAsync(ct);

		foreach (var row in legacy)
		{
			string chatId = row.ChatId.ToString(CultureInfo.InvariantCulture);
			if (!activity.TryGetValue(chatId, out DateTime existing) || row.Last > existing)
			{
				activity[chatId] = row.Last;
			}
		}

		return activity;
	}

	/// <summary>The response for an already-fixed session: its own stored boundary, priority set and a boundary cursor to enumerate the rest.</summary>
	private async Task<SyncHandshakeResponse> DescribeAsync(SyncSessionEntity session, DateTime now, CancellationToken ct)
	{
		List<string> priorityChatIds = await db.SyncSessionBoundaries.AsNoTracking()
			.Where(b => b.SyncSessionId == session.Id && b.PriorityRank != null)
			.OrderBy(b => b.PriorityRank)
			.ThenBy(b => b.Ordinal)
			.Select(b => b.ChatId)
			.ToListAsync(ct);

		// "The response contains the catalog boundary, bounded priority IDs and BoundaryCursor (null only
		// when there are no chat boundaries)." A repeated handshake mints its own cursor over the SAME
		// fixed set: the token is opaque and position-free, so two of them simply enumerate the same rows.
		string? boundaryCursor = session.BoundaryCount == 0
			? null
			: cursors.Mint(session, SyncCursorSurface.Boundary, SyncStreamKind.Catalog, null, null, 0, session.BoundaryCount - 1, now).Id;

		return new SyncHandshakeResponse(
			SyncProtocol.Version,
			session.Id,
			session.ConnectionId,
			Seq(session.RightsVersion),
			session.Mode == SyncSessionMode.Full ? SyncMode.Full : SyncMode.Delta,
			new SyncBoundary(null, CatalogPosition(session.CatalogThroughSequence), session.SnapshotId),
			[.. priorityChatIds.Distinct()],
			boundaryCursor,
			RfcTimestamp.Format(now));
	}

	public async Task<SyncResult<SyncBoundaryPage>> GetBoundaryPageAsync(
		ClaimsPrincipal caller, SyncBoundaryPageRequest request, CancellationToken ct = default)
	{
		if (request is null)
		{
			return SyncResult<SyncBoundaryPage>.Fail(SyncErrorCode.InvalidRequest);
		}

		if (request.ProtocolVersion != SyncProtocol.Version)
		{
			return SyncResult<SyncBoundaryPage>.Fail(SyncErrorCode.UnsupportedVersion);
		}

		if (request.Limit < 1 || request.Limit > SyncProtocol.MaxBoundaryPageItems)
		{
			// "Limit is 1..25" - an out-of-range limit is a malformed request, not something to clamp
			// silently, because the caller's own paging arithmetic depends on what it asked for.
			return SyncResult<SyncBoundaryPage>.Fail(SyncErrorCode.InvalidRequest);
		}

		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		SyncSessionGate.Context context = await SyncSessionGate.OpenAsync(db, caller, request.SyncSessionId, now, ct);
		if (context.Failure is SyncErrorCode gateFailure)
		{
			return SyncResult<SyncBoundaryPage>.Fail(gateFailure);
		}

		SyncSessionEntity session = context.Session!;
		SyncCursorEntity? cursor = await cursors.ResolveAsync(request.Cursor, now, ct);
		if (cursor is null || !SyncSessionGate.Binds(cursor, session, SyncCursorSurface.Boundary, null, null))
		{
			return SyncResult<SyncBoundaryPage>.Fail(SyncErrorCode.CursorInvalid);
		}

		long from = cursor.FromSequence;

		if (cursor.IssuedThroughSequence is long issued)
		{
			// An already-answered cursor re-answers EXACTLY what it answered: the same ordinal range and
			// the same continuation token, which is what makes a retry after a lost response consequence-free.
			List<SyncBoundary> replay = [.. (await ReadBoundaryRowsAsync(session.Id, from, issued, ct))
				.Select(row => ToBoundary(row, session.SnapshotId))];

			return SyncResult<SyncBoundaryPage>.Ok(new SyncBoundaryPage(
				session.Id, replay, cursor.NextCursorId, cursor.IsTerminal));
		}

		long through = Math.Min(cursor.UpperBoundSequence, from + request.Limit - 1);
		List<SyncSessionBoundaryEntity> rows = await ReadBoundaryRowsAsync(session.Id, from, through, ct);
		List<SyncBoundary> boundaries = [.. rows.Select(row => ToBoundary(row, session.SnapshotId))];

		// The hub frame carries this page whole, so the byte cap is enforced on what is actually sent -
		// not on an item count that happens to look small. The placeholder stands for a real cursor
		// token, which is always 43 characters.
		string placeholder = new('A', 43);
		while (boundaries.Count > 1 && JsonSerializer.SerializeToUtf8Bytes(
			new SyncBoundaryPage(session.Id, boundaries, placeholder, false), SyncJson).Length > SyncProtocol.MaxHubMessageBytes)
		{
			boundaries.RemoveAt(boundaries.Count - 1);
			through = rows[boundaries.Count - 1].Ordinal;
		}

		bool complete = through >= cursor.UpperBoundSequence;
		string? nextToken = null;

		cursor.IssuedThroughSequence = through;
		cursor.IsTerminal = complete;
		if (!complete)
		{
			SyncCursorEntity next = cursors.Mint(
				session, SyncCursorSurface.Boundary, SyncStreamKind.Catalog, null, null, through + 1, cursor.UpperBoundSequence, now);
			cursor.NextCursorId = next.Id;
			nextToken = next.Id;
		}

		await db.SaveChangesAsync(ct);

		return SyncResult<SyncBoundaryPage>.Ok(new SyncBoundaryPage(session.Id, boundaries, nextToken, complete));
	}

	private Task<List<SyncSessionBoundaryEntity>> ReadBoundaryRowsAsync(string sessionId, long from, long through, CancellationToken ct) =>
		db.SyncSessionBoundaries.AsNoTracking()
			.Where(b => b.SyncSessionId == sessionId && b.Ordinal >= from && b.Ordinal <= through)
			.OrderBy(b => b.Ordinal)
			.ToListAsync(ct);

	private static SyncBoundary ToBoundary(SyncSessionBoundaryEntity row, string snapshotId) =>
		new(new MembershipScope(row.ChatId, row.MembershipId, Seq(row.RightsVersion)),
			ChatPosition(row.ChatId, row.MembershipId, row.ThroughChangeSequence),
			snapshotId);

	public async Task<SyncResult<SyncPositionBatchResult>> ExchangePositionsAsync(
		ClaimsPrincipal caller, string connectionId, SyncPositionBatch batch, CancellationToken ct = default)
	{
		if (batch?.Positions is null || string.IsNullOrEmpty(connectionId))
		{
			return SyncResult<SyncPositionBatchResult>.Fail(SyncErrorCode.InvalidRequest);
		}

		if (batch.ProtocolVersion != SyncProtocol.Version)
		{
			return SyncResult<SyncPositionBatchResult>.Fail(SyncErrorCode.UnsupportedVersion);
		}

		if (batch.BatchIndex < 0
			|| !RestrictedAsciiId.IsValid(batch.ExchangeId)
			|| batch.Positions.Count > SyncProtocol.MaxPositionBatchItems
			|| !Enum.IsDefined(batch.Kind)
			|| batch.Positions.Any(p => !IsWellFormed(p))
			|| !string.Equals(batch.ConnectionId, connectionId, StringComparison.Ordinal))
		{
			return SyncResult<SyncPositionBatchResult>.Fail(SyncErrorCode.InvalidRequest);
		}

		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		SyncSessionGate.Context context = await SyncSessionGate.OpenAsync(db, caller, batch.SyncSessionId, now, ct);
		if (context.Failure is SyncErrorCode gateFailure)
		{
			return SyncResult<SyncPositionBatchResult>.Fail(gateFailure);
		}

		SyncSessionEntity session = context.Session!;

		// "Exchange identity is bound to sync session, connection, rights and kind." A reconnect produces
		// a new connection, which invalidates every readiness confirmation of the old one.
		if (!string.Equals(session.ConnectionId, connectionId, StringComparison.Ordinal))
		{
			return SyncResult<SyncPositionBatchResult>.Fail(SyncErrorCode.InvalidRequest);
		}

		if (!string.Equals(batch.RightsVersion, Seq(session.RightsVersion), StringComparison.Ordinal))
		{
			return SyncResult<SyncPositionBatchResult>.Fail(SyncErrorCode.RightsChanged);
		}

		SyncPositionBatchResult? answer = null;
		SyncErrorCode? failure = null;

		TrustCheckResult trust = await TrustedMutation.RunAsync(db, trustChecker, caller, async () =>
		{
			// The session row itself serializes the batches of one exchange: two batches racing on the
			// same session (a retry overlapping the next index) cannot both read "no earlier Final" and
			// both write.
			await db.Database.ExecuteSqlInterpolatedAsync(
				$"""SELECT 1 FROM "SyncSessions" WHERE "Id" = {session.Id} FOR UPDATE""", ct);

			string digest = Digest(batch);

			SyncPositionExchangeEntity? stored = await db.SyncPositionExchanges.AsNoTracking()
				.FirstOrDefaultAsync(e => e.SyncSessionId == session.Id && e.ExchangeId == batch.ExchangeId && e.BatchIndex == batch.BatchIndex, ct);

			if (stored is not null)
			{
				// "Identical retries return the saved result; a changed batch with the same exchange/index
				// ... fails InvalidRequest."
				if (!string.Equals(stored.RequestDigest, digest, StringComparison.Ordinal))
				{
					failure = SyncErrorCode.InvalidRequest;
					return;
				}

				answer = JsonSerializer.Deserialize<SyncPositionBatchResult>(stored.ResultJson, SyncJson);
				return;
			}

			List<SyncPositionExchangeEntity> earlier = await db.SyncPositionExchanges.AsNoTracking()
				.Where(e => e.SyncSessionId == session.Id && e.ExchangeId == batch.ExchangeId)
				.OrderBy(e => e.BatchIndex)
				.ToListAsync(ct);

			// "starts at BatchIndex=0, increments by one and ends with Final=true" - a gap, a repeated
			// kind change, or anything after Final is refused rather than guessed.
			if (earlier.Count != batch.BatchIndex
				|| earlier.Any(e => e.Final)
				|| earlier.Any(e => e.Kind != (SyncPositionExchangeKind)batch.Kind))
			{
				failure = SyncErrorCode.InvalidRequest;
				return;
			}

			ExchangeApplication application = await ApplyPositionsAsync(session, batch, now, ct);
			if (application.Failure is SyncErrorCode applicationFailure)
			{
				failure = applicationFailure;
				return;
			}

			bool complete = batch.Final;
			if (complete && batch.Kind == PositionExchangeKind.Resume)
			{
				// "The server records the completed resume set before declaring readiness; partial uploads
				// cannot complete initial restoration."
				session.ResumeCompletedAtUtc = now;
			}

			SyncPositionBatchResult result = new(
				batch.ExchangeId, batch.Kind, batch.BatchIndex, complete, application.ServerPositions, application.DataReadyScopes);

			db.SyncPositionExchanges.Add(new SyncPositionExchangeEntity(
				session.Id, batch.ExchangeId, batch.BatchIndex, (SyncPositionExchangeKind)batch.Kind,
				connectionId, session.RightsVersion, digest, JsonSerializer.Serialize(result, SyncJson), batch.Final, now));

			await db.SaveChangesAsync(ct);

			// Reaching full readiness in a Full session is what completes the initial restoration for this
			// exact SQLite copy - and therefore what makes every later handshake a Delta one.
			if (session.Mode == SyncSessionMode.Full && await IsLiveReadyAsync(session, ct))
			{
				SyncLocalInstanceEntity instance = await db.SyncLocalInstances.FirstAsync(i => i.Id == session.LocalInstanceId, ct);
				instance.InitialRestoreCompletedAtUtc ??= now;
				await db.SaveChangesAsync(ct);
			}

			answer = result;
		}, ct);

		if (!trust.IsTrusted)
		{
			return SyncResult<SyncPositionBatchResult>.Fail(SyncErrorCode.SessionExpired);
		}

		return failure is SyncErrorCode code
			? SyncResult<SyncPositionBatchResult>.Fail(code)
			: answer is null
				? SyncResult<SyncPositionBatchResult>.Fail(SyncErrorCode.Unavailable)
				: SyncResult<SyncPositionBatchResult>.Ok(answer);
	}

	private sealed record ExchangeApplication(
		IReadOnlyList<StreamPosition> ServerPositions, IReadOnlyList<MembershipScope> DataReadyScopes, SyncErrorCode? Failure);

	/// <summary>
	/// Records what this batch means for its kind, and answers the server's own positions for exactly the
	/// submitted streams, in order. All three kinds share this transport; none of them shares its meaning
	/// with another.
	/// </summary>
	private async Task<ExchangeApplication> ApplyPositionsAsync(
		SyncSessionEntity session, SyncPositionBatch batch, DateTime now, CancellationToken ct)
	{
		List<SyncSessionBoundaryEntity> boundaries = await db.SyncSessionBoundaries
			.Where(b => b.SyncSessionId == session.Id)
			.ToListAsync(ct);

		List<StreamPosition> serverPositions = [];
		List<MembershipScope> dataReady = [];

		foreach (StreamPosition position in batch.Positions)
		{
			if (!TryParseSeq(position.Sequence, out long submitted))
			{
				return new ExchangeApplication([], [], SyncErrorCode.InvalidRequest);
			}

			if (position.StreamKind == StreamKind.Catalog)
			{
				if (submitted > session.CatalogThroughSequence && batch.Kind != PositionExchangeKind.Check)
				{
					// A position past this session's own fixed bound cannot be acknowledged against it.
					return new ExchangeApplication([], [], SyncErrorCode.InvalidRequest);
				}

				ApplyCatalog(session, batch.Kind, submitted);
				serverPositions.Add(CatalogPosition(batch.Kind == PositionExchangeKind.Check
					? await SyncSessionGate.CurrentCatalogPositionAsync(db, session.AccountId, ct)
					: session.CatalogThroughSequence));
				continue;
			}

			SyncSessionBoundaryEntity? boundary = boundaries.FirstOrDefault(b =>
				string.Equals(b.ChatId, position.ChatId, StringComparison.Ordinal)
				&& string.Equals(b.MembershipId, position.MembershipId, StringComparison.Ordinal));

			if (boundary is null)
			{
				// A stream this session holds no boundary for: the device may still be carrying a position
				// for a scope it has lost. It is echoed with the only authoritative answer there is -
				// nothing is permitted here - and never counted towards readiness.
				serverPositions.Add(ChatPosition(position.ChatId!, position.MembershipId!, 0));
				continue;
			}

			if (submitted > boundary.ThroughChangeSequence && batch.Kind != PositionExchangeKind.Check)
			{
				return new ExchangeApplication([], [], SyncErrorCode.InvalidRequest);
			}

			switch (batch.Kind)
			{
				case PositionExchangeKind.Resume:
					// "Resume must include every persisted local stream position (omitted newly authorized
					// streams start at zero)" - what arrives is recorded as-is; it is the device's own claim
					// about what it holds, and nothing is declared ready by it.
					boundary.UploadedSequence = submitted;
					break;

				case PositionExchangeKind.Acknowledge:
					if (submitted < (boundary.AcknowledgedSequence ?? 0))
					{
						// Continuity: an acknowledgement may only move forward. A lower one would silently
						// un-apply a scope that was already confirmed.
						return new ExchangeApplication([], [], SyncErrorCode.InvalidRequest);
					}

					boundary.AcknowledgedSequence = submitted;
					boundary.AcknowledgedAtUtc = now;
					if (submitted >= boundary.ThroughChangeSequence)
					{
						dataReady.Add(new MembershipScope(boundary.ChatId, boundary.MembershipId, Seq(boundary.RightsVersion)));
					}

					break;

				case PositionExchangeKind.Check:
				default:
					// Comparison only: nothing is recorded, and a difference is the device's signal to catch up.
					break;
			}

			serverPositions.Add(ChatPosition(
				boundary.ChatId,
				boundary.MembershipId,
				batch.Kind == PositionExchangeKind.Check
					? await CurrentChatPositionAsync(boundary, ct)
					: boundary.ThroughChangeSequence));
		}

		return new ExchangeApplication(serverPositions, dataReady, null);
	}

	private static void ApplyCatalog(SyncSessionEntity session, PositionExchangeKind kind, long submitted)
	{
		switch (kind)
		{
			case PositionExchangeKind.Resume:
				session.CatalogUploadedSequence = submitted;
				break;

			case PositionExchangeKind.Acknowledge:
				// Monotone, exactly like a chat scope's: an acknowledgement may only move forward.
				if (submitted >= (session.CatalogAcknowledgedSequence ?? 0))
				{
					session.CatalogAcknowledgedSequence = submitted;
				}

				break;

			case PositionExchangeKind.Check:
			default:
				// Comparison only - nothing is recorded.
				break;
		}
	}

	/// <summary>A frozen scope's authoritative position is its own frozen bound - its archive cannot grow - while an active one's is the chat's current end.</summary>
	private async Task<long> CurrentChatPositionAsync(SyncSessionBoundaryEntity boundary, CancellationToken ct) =>
		boundary.IsFrozen
			? boundary.ThroughChangeSequence
			: await db.SyncChatSequenceCounters.AsNoTracking()
				.Where(c => c.ChatId == boundary.ChatId)
				.Select(c => c.NextChangeSequence - 1)
				.FirstOrDefaultAsync(ct);

	public async Task<SyncResult<SyncReadiness>> GetReadinessAsync(
		ClaimsPrincipal caller, string connectionId, string syncSessionId, CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		SyncSessionGate.Context context = await SyncSessionGate.OpenAsync(db, caller, syncSessionId, now, ct);
		if (context.Failure is SyncErrorCode gateFailure)
		{
			return SyncResult<SyncReadiness>.Fail(gateFailure);
		}

		SyncSessionEntity session = context.Session!;

		// "Reconnect or RightsChanged invalidates the exchange and all readiness confirmations" - a
		// readiness question from another connection is not this session's readiness.
		bool liveReady = string.Equals(session.ConnectionId, connectionId, StringComparison.Ordinal)
			&& await IsLiveReadyAsync(session, ct);

		bool restoreComplete = await db.SyncLocalInstances.AsNoTracking()
			.Where(i => i.Id == session.LocalInstanceId)
			.Select(i => i.InitialRestoreCompletedAtUtc != null)
			.FirstOrDefaultAsync(ct);

		return SyncResult<SyncReadiness>.Ok(new SyncReadiness(
			session.Id, session.ConnectionId, Seq(session.RightsVersion), liveReady, restoreComplete));
	}

	/// <summary>
	/// Readiness is "every required scope acknowledged through its fixed boundary, the catalog
	/// acknowledged through its own, and the resume exchange complete" - never a single page's doing.
	/// </summary>
	private async Task<bool> IsLiveReadyAsync(SyncSessionEntity session, CancellationToken ct)
	{
		if (session.ResumeCompletedAtUtc is null || (session.CatalogAcknowledgedSequence ?? -1) < session.CatalogThroughSequence)
		{
			return false;
		}

		return !await db.SyncSessionBoundaries.AsNoTracking()
			.AnyAsync(b => b.SyncSessionId == session.Id
				&& (b.AcknowledgedSequence == null || b.AcknowledgedSequence < b.ThroughChangeSequence), ct);
	}

	/// <summary>Identifies one exact batch: the kind, the final flag and the submitted positions in their submitted order.</summary>
	private static string Digest(SyncPositionBatch batch)
	{
		StringBuilder builder = new();
		builder.Append(batch.Kind).Append('|').Append(batch.Final).Append('|');
		foreach (StreamPosition position in batch.Positions)
		{
			builder.Append(position.StreamKind).Append(':')
				.Append(position.ChatId).Append(':')
				.Append(position.MembershipId).Append(':')
				.Append(position.Sequence).Append(';');
		}

		return Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}
}
