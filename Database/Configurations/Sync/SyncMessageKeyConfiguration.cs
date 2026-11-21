using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMessageKeyConfiguration : IEntityTypeConfiguration<SyncMessageKeyEntity>
{
	public void Configure(EntityTypeBuilder<SyncMessageKeyEntity> builder)
	{
		builder.ToTable("SyncMessageKeys");

		builder.HasKey(e => e.MessageId);
		builder.Property(e => e.MessageId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.FormatVersion).HasMaxLength(20).IsRequired();
		builder.Property(e => e.Purpose).HasMaxLength(50).IsRequired();
		builder.Property(e => e.KeyVersion).HasMaxLength(50).IsRequired();

		builder.Property(e => e.ProtectedKey).IsRequired();
		builder.Property(e => e.Nonce).IsRequired();
		builder.Property(e => e.Tag).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Composite FK (ChatId, MessageId) -> SyncMessages(ChatId, Id), anchored on the existing
		// AK_SyncMessages_ChatId_Id alternate key - mirrors the SenderMembership composite-FK pattern
		// in SyncMessageConfiguration.cs (same codebase, same file family), so ChatId can never
		// disagree with the real chat of the message this protected key belongs to. Replaces two
		// independent single-column FKs (MessageId -> SyncMessages.Id, ChatId -> SyncChats.Id) that
		// gave the database no way to enforce that relationship - ChatId is not cosmetic, it is the
		// AES-GCM AAD's owner-context/authorization scope (see SyncMessageKeyEntity's own doc
		// comment). The explicit unique index is required for a 1:1 (WithOne) relationship whose FK
		// is not exactly the dependent's own PK (PK stays MessageId alone; FK is the wider
		// (ChatId, MessageId) tuple, which the PK already makes unique as a subset, but EF Core's
		// 1:1 configuration still requires it be declared explicitly).
		builder.HasIndex(e => new { e.ChatId, e.MessageId }).IsUnique().HasDatabaseName("IX_SyncMessageKeys_ChatId_MessageId");

		builder
			.HasOne(e => e.Message)
			.WithOne(e => e.Key)
			.HasForeignKey<SyncMessageKeyEntity>(e => new { e.ChatId, e.MessageId })
			.HasPrincipalKey<Database.Entities.Sync.SyncMessageEntity>(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncMessageKeys_ChatId_MessageId_SyncMessages_ChatId_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
