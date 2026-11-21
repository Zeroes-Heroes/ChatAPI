using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncAuthorKeyEscrowConfiguration : IEntityTypeConfiguration<SyncAuthorKeyEscrowEntity>
{
	public void Configure(EntityTypeBuilder<SyncAuthorKeyEscrowEntity> builder)
	{
		builder.ToTable("SyncAuthorKeyEscrows", tb =>
		{
			tb.HasCheckConstraint("CK_SyncAuthorKeyEscrows_NonceLength", "octet_length(\"Nonce\") = 12");
			tb.HasCheckConstraint("CK_SyncAuthorKeyEscrows_TagLength", "octet_length(\"Tag\") = 16");
			tb.HasCheckConstraint("CK_SyncAuthorKeyEscrows_RecoveryVersionPositive", "\"RecoveryVersion\" >= 1");
		});

		// One entry per author key per recovery version: re-uploading the same key is a no-op, not a
		// second copy with different bytes.
		builder.HasKey(e => new { e.AccountId, e.RecoveryVersion, e.AuthorKeyId });

		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.AuthorKeyId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.AuthorKeyVersion).HasMaxLength(20).IsRequired();
		builder.Property(e => e.FormatVersion).HasMaxLength(20).IsRequired();
		builder.Property(e => e.MasterKeyVersion).HasMaxLength(50).IsRequired();
		builder.Property(e => e.EphemeralSpki).IsRequired();
		builder.Property(e => e.Nonce).IsRequired();
		builder.Property(e => e.ProtectedPayload).IsRequired();
		builder.Property(e => e.Tag).IsRequired();
		builder.Property(e => e.CreatedByDeviceRegistrationId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncAuthorKeyEscrows_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// An entry is meaningless without the envelope holding the private half it was sealed to, and
		// envelopes are never deleted - so Restrict, like everything else in the recovery model.
		builder
			.HasOne<SyncRecoveryEnvelopeEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.RecoveryVersion })
			.HasConstraintName("FK_SyncAuthorKeyEscrows_AccountId_RecoveryVersion_SyncRecoveryEnvelopes")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne<SyncDeviceRegistrationEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.CreatedByDeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncAuthorKeyEscrows_AccountId_CreatedBy_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
