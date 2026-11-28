using Database.Entities.Sync;
using Services.Sync.Contracts;
using System.Globalization;

namespace Services.Sync.Models;

/// <summary>
/// One chat scope as a page actually works with it: the stint, its fixed upper bound and whether it is
/// an archive. Built from a session's own boundary row, never from anything a request supplied, so a
/// page can only ever read inside what the handshake fixed.
/// </summary>
/// <param name="IsFrozen">The stint has ended: metadata and statuses are resolved at ITS snapshot, not at today's state, and the bound cannot move.</param>
public sealed record SyncScopeContext(
	string ChatId,
	string MembershipId,
	long RightsVersion,
	long ThroughChangeSequence,
	bool IsFrozen,
	string SnapshotId)
{
	public static SyncScopeContext Of(SyncSessionBoundaryEntity boundary, string snapshotId) =>
		new(boundary.ChatId, boundary.MembershipId, boundary.RightsVersion, boundary.ThroughChangeSequence, boundary.IsFrozen, snapshotId);

	public MembershipScope Scope => new(ChatId, MembershipId, RightsVersion.ToString(CultureInfo.InvariantCulture));
}
