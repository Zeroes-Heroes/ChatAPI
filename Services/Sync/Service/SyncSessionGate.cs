using System.Security.Claims;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Sync.Contracts;
using Services.Sync.Models;

namespace Services.Sync.Service;

/// <summary>
/// The preamble every sync surface shares - the handshake's siblings and all three HTTP pages: resolve
/// the caller from its own claims, resolve the session it names as that caller's own, and revalidate the
/// current rights. One implementation, so no surface can accidentally be laxer than another.
/// </summary>
internal static class SyncSessionGate
{
	internal sealed record Context(SyncSessionEntity? Session, SyncCallerIdentity? Caller, SyncErrorCode? Failure);

	public static async Task<Context> OpenAsync(
		AppDbContext db, ClaimsPrincipal caller, string? syncSessionId, DateTime now, CancellationToken ct)
	{
		SyncCallerIdentity? identity = await SyncCallerIdentity.ResolveAsync(db, caller, ct);
		if (identity is null)
		{
			return new Context(null, null, SyncErrorCode.SessionExpired);
		}

		if (string.IsNullOrEmpty(syncSessionId))
		{
			return new Context(null, identity, SyncErrorCode.InvalidRequest);
		}

		SyncSessionEntity? session = await db.SyncSessions
			.FirstOrDefaultAsync(s => s.Id == syncSessionId
				&& s.AccountId == identity.AccountContextId
				&& s.DeviceRegistrationId == identity.DeviceRegistrationId
				&& s.LocalInstanceId == identity.LocalInstanceId
				&& s.DeviceSessionId == identity.DeviceSessionId
				&& s.EndedAtUtc == null, ct);

		if (session is null || session.ExpiresAtUtc <= now)
		{
			// Includes a session belonging to another device or another account context: it is simply not
			// this caller's session, and is refused identically to an expired one.
			return new Context(null, identity, SyncErrorCode.SessionExpired);
		}

		long rightsVersion = await CurrentCatalogPositionAsync(db, identity.AccountContextId, ct);
		return rightsVersion != session.RightsVersion
			// "Revalidate session/current rights on every page", and "A later rights change invalidates
			// this enumeration rather than mixing snapshots".
			? new Context(null, identity, SyncErrorCode.RightsChanged)
			: new Context(session, identity, null);
	}

	/// <summary>The account's current catalog position, which is also its current rights version (see <see cref="SyncSessionEntity.RightsVersion"/>).</summary>
	public static Task<long> CurrentCatalogPositionAsync(AppDbContext db, string accountId, CancellationToken ct) =>
		db.SyncAccountCatalogCounters.AsNoTracking()
			.Where(c => c.AccountId == accountId)
			.Select(c => c.NextCatalogSequence - 1)
			.FirstOrDefaultAsync(ct);

	/// <summary>Whether a cursor really belongs to this session, this surface and this scope - a token is a name, never an authority.</summary>
	public static bool Binds(
		SyncCursorEntity cursor, SyncSessionEntity session, SyncCursorSurface surface, string? chatId, string? membershipId) =>
		string.Equals(cursor.SyncSessionId, session.Id, StringComparison.Ordinal)
		&& string.Equals(cursor.AccountId, session.AccountId, StringComparison.Ordinal)
		&& string.Equals(cursor.DeviceRegistrationId, session.DeviceRegistrationId, StringComparison.Ordinal)
		&& string.Equals(cursor.LocalInstanceId, session.LocalInstanceId, StringComparison.Ordinal)
		&& string.Equals(cursor.SnapshotId, session.SnapshotId, StringComparison.Ordinal)
		&& cursor.RightsVersion == session.RightsVersion
		&& cursor.Surface == surface
		&& string.Equals(cursor.ChatId, chatId, StringComparison.Ordinal)
		&& string.Equals(cursor.MembershipId, membershipId, StringComparison.Ordinal);
}
