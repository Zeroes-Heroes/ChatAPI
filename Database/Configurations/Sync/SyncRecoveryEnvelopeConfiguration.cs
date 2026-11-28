using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncRecoveryEnvelopeConfiguration : IEntityTypeConfiguration<SyncRecoveryEnvelopeEntity>
{
	public void Configure(EntityTypeBuilder<SyncRecoveryEnvelopeEntity> builder)
	{
		builder.ToTable("SyncRecoveryEnvelopes", tb =>
		{
			// Same pinned KDF bounds Services/Recovery/Models/RecoveryKdfMetadata.cs enforces, at the
			// database level too, so a caller bypassing the service still cannot persist metadata a
			// client would refuse to derive from.
			tb.HasCheckConstraint("CK_SyncRecoveryEnvelopes_KdfVersion", "\"KdfVersion\" = 19");
			tb.HasCheckConstraint("CK_SyncRecoveryEnvelopes_SaltLength", "octet_length(\"Salt\") = 16");
			tb.HasCheckConstraint("CK_SyncRecoveryEnvelopes_NonceLength", "octet_length(\"Nonce\") = 12");
			tb.HasCheckConstraint("CK_SyncRecoveryEnvelopes_TagLength", "octet_length(\"Tag\") = 16");
			tb.HasCheckConstraint("CK_SyncRecoveryEnvelopes_ResourceBounds",
				"\"MemoryKiB\" BETWEEN 8192 AND 1048576 AND \"Iterations\" BETWEEN 1 AND 10 AND \"Parallelism\" BETWEEN 1 AND 4");
			tb.HasCheckConstraint("CK_SyncRecoveryEnvelopes_RecoveryVersionPositive", "\"RecoveryVersion\" >= 1");
			tb.HasCheckConstraint("CK_SyncRecoveryEnvelopes_ConfirmedNotBeforeCreated", "\"ConfirmedAtUtc\" IS NULL OR \"ConfirmedAtUtc\" >= \"CreatedAtUtc\"");
		});

		// Composite key, not a surrogate id: one row per (account context, recovery version), and the
		// version is part of the client's own AAD - there is no second identity to invent.
		builder.HasKey(e => new { e.AccountId, e.RecoveryVersion });

		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.FormatVersion).HasMaxLength(20).IsRequired();
		builder.Property(e => e.KdfAlgorithm).HasMaxLength(50).IsRequired();
		builder.Property(e => e.MasterKeyVersion).HasMaxLength(50).IsRequired();
		builder.Property(e => e.Salt).IsRequired();
		builder.Property(e => e.VerificationSpki).IsRequired();
		builder.Property(e => e.Nonce).IsRequired();
		builder.Property(e => e.ProtectedEnvelope).IsRequired();
		builder.Property(e => e.Tag).IsRequired();
		builder.Property(e => e.CreatedByDeviceRegistrationId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Supports "the newest confirmed version for this account".
		builder.HasIndex(e => new { e.AccountId, e.ConfirmedAtUtc }).HasDatabaseName("IX_SyncRecoveryEnvelopes_AccountId_ConfirmedAtUtc");

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncRecoveryEnvelopes_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// The uploading device must belong to the same account - same composite-FK discipline as
		// SyncDeviceChallenges. Restrict (not Cascade): losing the device that happened to upload an
		// envelope must never delete the envelope a recovering device still needs.
		builder
			.HasOne<SyncDeviceRegistrationEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.CreatedByDeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncRecoveryEnvelopes_AccountId_CreatedBy_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
