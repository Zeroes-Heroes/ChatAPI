using Database.Context;
using Microsoft.EntityFrameworkCore;

namespace Services.Sessions.Service;

/// <summary>
/// The one rule for classifying an id (DeviceRegistrationId / LocalInstanceId) that already belongs to
/// a DIFFERENT account context than the one being enrolled into. Both ids are unique across the whole
/// server, so the owning context decides the answer: an ENDED context of the same verified identity is
/// the stage-06 fresh-start case the install can resolve itself, anything else is simply not its id.
/// </summary>
internal static class AccountContextOwnership
{
	/// <summary>
	/// True when <paramref name="ownerAccountId"/> is an already-ended context of the same
	/// LegacyUserId (verified phone identity) as <paramref name="accountId"/> - i.e. a fresh start
	/// replaced the context this install still holds ids from. False for a live context and for any
	/// other identity's context, so another phone's ids are never distinguishable from unknown ones.
	/// </summary>
	public static async Task<bool> IsSupersededContextOfSameIdentityAsync(
		AppDbContext db, string accountId, string ownerAccountId, CancellationToken ct)
	{
		var current = await db.SyncAccounts.AsNoTracking()
			.Where(a => a.Id == accountId)
			.Select(a => new { a.LegacyUserId })
			.SingleAsync(ct);

		return await db.SyncAccounts.AsNoTracking()
			.AnyAsync(a => a.Id == ownerAccountId && a.EndedAtUtc != null && a.LegacyUserId == current.LegacyUserId, ct);
	}
}
