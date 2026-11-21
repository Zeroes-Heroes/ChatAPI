using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;

namespace Services.Sessions.Service;

/// <summary>
/// The one place a device's local copy is recorded while a session is being issued (both enrollment
/// coordinators). A LocalInstanceId is unique across the whole server - the table's primary key is the
/// id alone - and therefore belongs to exactly one DeviceRegistrationId, so a copy already recorded
/// under a different registration (e.g. an install reusing the ids of the context a fresh start
/// replaced) is refused here rather than failing as a key violation. The client's answer is a new local
/// instance id, which is what it genuinely is by then (Services/Session/LocalInstanceIdentity.ts) - and
/// when the current owner's registration lives in an ENDED context of this same identity, the refusal
/// says exactly that, the same distinction <see cref="DeviceChallengeService"/> makes for a registration id.
/// </summary>
internal static class LocalInstanceRegistrar
{
	public static async Task EnsureRecordedAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string localInstanceId, DateTime now, CancellationToken ct)
	{
		SyncLocalInstanceEntity? instance = await db.SyncLocalInstances
			.FirstOrDefaultAsync(i => i.DeviceRegistrationId == deviceRegistrationId && i.Id == localInstanceId, ct);
		if (instance is not null)
		{
			return;
		}

		string? ownerAccountId = await db.SyncLocalInstances.AsNoTracking()
			.Where(i => i.Id == localInstanceId)
			.Select(i => i.DeviceRegistration.AccountId)
			.FirstOrDefaultAsync(ct);
		if (ownerAccountId is not null)
		{
			if (await AccountContextOwnership.IsSupersededContextOfSameIdentityAsync(db, accountId, ownerAccountId, ct))
			{
				throw new DeviceIdentitySupersededException("This local instance id belongs to a context this identity has since replaced.");
			}

			throw new SessionNotTrustedException("This local instance id already belongs to another device registration.");
		}

		db.SyncLocalInstances.Add(new SyncLocalInstanceEntity(localInstanceId, deviceRegistrationId, now));
	}
}
