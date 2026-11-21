namespace Database.Entities.Sync;

/// <summary>
/// Root identity for the sync protocol's AccountContextId. Bridges 1:1 to the existing
/// authenticated UserEntity (LegacyUserId) so auth/session code can bind into this model;
/// every other sync table hangs off this root rather than off UserEntity directly, keeping the
/// new protocol's tables additive and independent of legacy Chat/Message schema changes.
///
/// LegacyUserId is unique only among currently-active contexts (EndedAtUtc is null), not
/// globally: docs/sync-contract-v1.md says "AccountContextId ... changes for a fresh start on the
/// same phone", and plan/06-recovery.md requires that without another trusted device "old context
/// stays blocked" - i.e. retained, not deleted. EndedAtUtc is the minimal lifecycle marker that
/// makes both true at once: ending a context (recorded here, schema-only - no recovery/auth
/// service lives in this stage) frees its LegacyUserId for exactly one fresh/current context,
/// while the ended row and everything hung off it remains in place and queryable.
/// </summary>
public class SyncAccountEntity
{
	public SyncAccountEntity(string id, int legacyUserId, DateTime createdAtUtc)
	{
		Id = id;
		LegacyUserId = legacyUserId;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncAccountEntity() { }

	public string Id { get; set; } = null!;

	public int LegacyUserId { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	/// <summary>Null means this is the current/active context for its LegacyUserId. Set once a fresh context supersedes it; the row and its data are retained, not deleted.</summary>
	public DateTime? EndedAtUtc { get; set; }

	/// <summary>
	/// Stage 05 (plan/05-device-sessions.md): set true the first time any device of this account is
	/// explicitly revoked, and never reset back to false - including once the revocation leaves zero
	/// currently-trusted devices. This is what stops "revoke the last trusted device" from silently
	/// turning the next registration attempt back into an unapproved "first device": the enrollment
	/// decision (<see cref="Services.Sessions.Interface.IDeviceTrustService"/>) requires
	/// DeviceApproval whenever this flag is true, even with zero currently-trusted devices, per
	/// docs/sync-contract-v1.md: "After explicit revocation, unknown/revoked registrations require
	/// another currently trusted device even with valid recovery. Without it old context stays
	/// blocked." A genuinely fresh start (plan/05-device-sessions.md's "ново начало") is a new
	/// AccountContextId (a new row, defaulting false again), never this flag clearing on the same row.
	/// </summary>
	public bool RevocationBarrierActive { get; set; }

	public virtual UserEntity LegacyUser { get; set; } = null!;
	public virtual ICollection<SyncDeviceRegistrationEntity> Devices { get; set; } = [];
	public virtual ICollection<SyncMembershipEntity> Memberships { get; set; } = [];
	public virtual SyncAccountCatalogCounterEntity? CatalogCounter { get; set; }
}
