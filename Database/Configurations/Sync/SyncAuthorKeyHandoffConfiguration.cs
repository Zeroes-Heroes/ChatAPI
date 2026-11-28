using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncAuthorKeyHandoffConfiguration : IEntityTypeConfiguration<SyncAuthorKeyHandoffEntity>
{
	public void Configure(EntityTypeBuilder<SyncAuthorKeyHandoffEntity> builder)
	{
		builder.ToTable("SyncAuthorKeyHandoffs", tb =>
		{
			tb.HasCheckConstraint("CK_SyncAuthorKeyHandoffs_NonceLength", "octet_length(\"Nonce\") = 12");
			tb.HasCheckConstraint("CK_SyncAuthorKeyHandoffs_TagLength", "octet_length(\"Tag\") = 16");
			tb.HasCheckConstraint("CK_SyncAuthorKeyHandoffs_DeliveredNotBeforeCreated",
				"\"DeliveredAtUtc\" IS NULL OR \"DeliveredAtUtc\" >= \"CreatedAtUtc\"");
		});

		builder.HasKey(e => e.ChallengeId);
		builder.Property(e => e.ChallengeId).HasMaxLength(128);
		builder.Property(e => e.AccountId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.PendingDeviceRegistrationId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.EphemeralPublicKeySpki).IsRequired();
		builder.Property(e => e.Nonce).IsRequired();
		builder.Property(e => e.Ciphertext).IsRequired();
		builder.Property(e => e.Tag).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Supports the one-use delivery claim (by device, undelivered).
		builder.HasIndex(e => new { e.AccountId, e.PendingDeviceRegistrationId, e.DeliveredAtUtc })
			.HasDatabaseName("IX_SyncAuthorKeyHandoffs_Account_Device_Delivered");

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncAuthorKeyHandoffs_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// 1:1 with the grant it accompanies: a handoff can only exist for an actually approved request.
		builder
			.HasOne(e => e.Grant)
			.WithOne()
			.HasForeignKey<SyncAuthorKeyHandoffEntity>(e => e.ChallengeId)
			.HasConstraintName("FK_SyncAuthorKeyHandoffs_ChallengeId_SyncDeviceApprovalGrants")
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne<SyncDeviceRegistrationEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.PendingDeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncAuthorKeyHandoffs_AccountId_Pending_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
