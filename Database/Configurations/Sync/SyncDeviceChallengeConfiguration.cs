using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncDeviceChallengeConfiguration : IEntityTypeConfiguration<SyncDeviceChallengeEntity>
{
	public void Configure(EntityTypeBuilder<SyncDeviceChallengeEntity> builder)
	{
		builder.ToTable("SyncDeviceChallenges", tb =>
		{
			tb.HasCheckConstraint("CK_SyncDeviceChallenges_ExpiresAfterCreated", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
			tb.HasCheckConstraint("CK_SyncDeviceChallenges_ConsumedNotBeforeCreated", "\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"CreatedAtUtc\"");
			// Purpose=2 is SessionBinding (Database.Enums.Sync.ChallengePurpose) - the only purpose
			// server-side-bound to a SessionId/RefreshOperationId pair per docs/sync-contract-v1.md.
			tb.HasCheckConstraint("CK_SyncDeviceChallenges_SessionBindingFieldsMatchPurpose",
				"(\"Purpose\" = 2 AND \"SessionId\" IS NOT NULL AND \"RefreshOperationId\" IS NOT NULL) OR (\"Purpose\" != 2 AND \"SessionId\" IS NULL AND \"RefreshOperationId\" IS NULL)");
		});

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(128);

		builder.Property(e => e.Purpose).IsRequired().HasConversion<int>();
		builder.Property(e => e.AccountId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.DeviceRegistrationId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.KeyId).IsRequired().HasMaxLength(128);
		builder.Property(e => e.PublicKeySpki).IsRequired();
		builder.Property(e => e.Nonce).IsRequired();
		builder.Property(e => e.ExpiresAtUtc).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();
		builder.Property(e => e.SessionId).HasMaxLength(100);
		builder.Property(e => e.RefreshOperationId).HasMaxLength(150);

		// One-use: the exact 256-bit value can never be issued twice, independent of the ChallengeId
		// the client sees - belt-and-braces alongside the ConsumedAtUtc atomic-claim column.
		builder.HasIndex(e => e.Nonce).IsUnique().HasDatabaseName("IX_SyncDeviceChallenges_Nonce");

		// Supports the atomic conditional UPDATE's WHERE clause (claim-by-id-and-unconsumed) and the
		// expiry sweep a future stage may add.
		builder.HasIndex(e => new { e.Id, e.ConsumedAtUtc }).HasDatabaseName("IX_SyncDeviceChallenges_Id_ConsumedAtUtc");

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncDeviceChallenges_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the device registration this challenge is bound to must belong to the same account.
		builder
			.HasOne(e => e.DeviceRegistration)
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.DeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncDeviceChallenges_AccountId_DeviceRegistrationId_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
