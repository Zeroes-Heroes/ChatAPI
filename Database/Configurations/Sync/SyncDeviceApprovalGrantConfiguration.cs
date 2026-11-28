using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncDeviceApprovalGrantConfiguration : IEntityTypeConfiguration<SyncDeviceApprovalGrantEntity>
{
	public void Configure(EntityTypeBuilder<SyncDeviceApprovalGrantEntity> builder)
	{
		builder.ToTable("SyncDeviceApprovalGrants", tb =>
		{
			tb.HasCheckConstraint("CK_SyncDeviceApprovalGrants_ConsumedNotBeforeCreated",
				"\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"CreatedAtUtc\"");
			tb.HasCheckConstraint("CK_SyncDeviceApprovalGrants_ExpiresAfterCreated", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
			// 64-byte IEEE P1363 R||S, per docs/sync-contract-v1.md - a DER-encoded or truncated
			// signature can never even be stored.
			tb.HasCheckConstraint("CK_SyncDeviceApprovalGrants_SignatureLength", "octet_length(\"Signature\") = 64");
			// A device approving its own pending registration would make the "another currently trusted
			// device" rule vacuous; the service checks this too, this is the database-level backstop.
			tb.HasCheckConstraint("CK_SyncDeviceApprovalGrants_ApproverIsNotRequester",
				"\"ApprovingDeviceRegistrationId\" <> \"PendingDeviceRegistrationId\"");
		});

		builder.HasKey(e => e.ChallengeId);
		builder.Property(e => e.ChallengeId).HasMaxLength(128);

		builder.Property(e => e.AccountId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.PendingDeviceRegistrationId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.PendingKeyId).HasMaxLength(128).IsRequired();
		builder.Property(e => e.PendingPublicKeySpki).IsRequired();
		builder.Property(e => e.PendingLocalInstanceId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.ApprovingDeviceRegistrationId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.ApprovingSessionId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.Signature).IsRequired();
		builder.Property(e => e.ExpiresAtUtc).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Supports the atomic conditional claim's WHERE clause (by id, unconsumed).
		builder.HasIndex(e => new { e.ChallengeId, e.ConsumedAtUtc }).HasDatabaseName("IX_SyncDeviceApprovalGrants_ChallengeId_ConsumedAtUtc");

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncDeviceApprovalGrants_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// 1:1 with the challenge it approves: the challenge row is the server-side record of the exact
		// pending request, and Cascade here means a deleted challenge can never leave an orphan grant
		// that a later register attempt could still find.
		builder
			.HasOne(e => e.Challenge)
			.WithOne()
			.HasForeignKey<SyncDeviceApprovalGrantEntity>(e => e.ChallengeId)
			.HasConstraintName("FK_SyncDeviceApprovalGrants_ChallengeId_SyncDeviceChallenges_Id")
			.OnDelete(DeleteBehavior.Cascade);

		// Both devices must belong to the same account as the grant.
		builder
			.HasOne<SyncDeviceRegistrationEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.PendingDeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncDeviceApprovalGrants_AccountId_Pending_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne<SyncDeviceRegistrationEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.ApprovingDeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncDeviceApprovalGrants_AccountId_Approving_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
