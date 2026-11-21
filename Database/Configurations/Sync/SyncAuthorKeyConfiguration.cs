using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncAuthorKeyConfiguration : IEntityTypeConfiguration<SyncAuthorKeyEntity>
{
	public void Configure(EntityTypeBuilder<SyncAuthorKeyEntity> builder)
	{
		builder.ToTable("SyncAuthorKeys", tb =>
			// A P-256 SubjectPublicKeyInfo is exactly 91 bytes; anything else is not the key shape the
			// verifier can import, and storing it would only move the failure to verification time.
			tb.HasCheckConstraint("CK_SyncAuthorKeys_SpkiLength", "octet_length(\"Spki\") = 91"));

		// The complete wire AuthorKeyReference IS the key: a new version is a new row (rotation), and the
		// same reference can therefore never hold two different public keys.
		builder.HasKey(e => new { e.AccountId, e.ChatId, e.AuthorKeyId, e.AuthorKeyVersion });

		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.AuthorKeyId).HasMaxLength(128);
		builder.Property(e => e.AuthorKeyVersion).HasMaxLength(20);
		builder.Property(e => e.Spki).IsRequired();
		builder.Property(e => e.RegisteredByDeviceRegistrationId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// The lookup every acceptance performs, and the only one: by the full reference (the primary key
		// above). This second index serves the per-chat enumeration a delivery/escrow path needs.
		builder.HasIndex(e => new { e.ChatId, e.AccountId });

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncAuthorKeys_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Restrict, like every other row that a message's verifiability depends on: a key whose chat is
		// gone could never be used again, but a message signed under it must stay checkable.
		builder
			.HasOne(e => e.Chat)
			.WithMany()
			.HasForeignKey(e => e.ChatId)
			.HasConstraintName("FK_SyncAuthorKeys_ChatId_SyncChats_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the registering device must belong to the account whose key this is - the same pin
		// the escrow table uses, so a key can never be attributed to another account's registration.
		builder
			.HasOne(e => e.RegisteredByDeviceRegistration)
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.RegisteredByDeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncAuthorKeys_AccountId_RegisteredBy_SyncDeviceRegs")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
