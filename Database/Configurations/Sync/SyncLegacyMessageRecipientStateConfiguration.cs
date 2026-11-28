using Database.Entities;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncLegacyMessageRecipientStateConfiguration : IEntityTypeConfiguration<SyncLegacyMessageRecipientStateEntity>
{
	public void Configure(EntityTypeBuilder<SyncLegacyMessageRecipientStateEntity> builder)
	{
		builder.ToTable("SyncLegacyMessageRecipientStates", tb =>
			// A reason exists exactly when a Seen time exists: the pair is what the stage fixes together
			// on the first accepted transition, and neither half is meaningful alone.
			tb.HasCheckConstraint(
				"CK_SyncLegacyMessageRecipientStates_SeenPair",
				"(\"SeenAtUtc\" IS NULL) = (\"SeenReason\" IS NULL)"));

		// One row per (stint, message): the person's logical state inside one access period.
		builder.HasKey(e => new { e.MembershipId, e.LegacyMessageId });

		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.Version).IsRequired();

		// The group proportions and the author's individual view both read "every recipient state of
		// this message", which is the opposite direction from the primary key.
		builder.HasIndex(e => new { e.ChatId, e.LegacyMessageId })
			.HasDatabaseName("IX_SyncLegacyMessageRecipientStates_ChatId_LegacyMessageId");

		// The composite FK's own columns; also "what does this person hold in this chat" by its leading
		// columns. Named explicitly: the conventional name would exceed PostgreSQL's 63-character limit
		// and be truncated at creation.
		builder.HasIndex(e => new { e.ChatId, e.AccountId, e.MembershipId })
			.HasDatabaseName("IX_SyncLegacyMsgRecipientStates_ChatId_AccountId_MembershipId");

		// Composite: the stint must belong to both this chat and this account at once, so no cross-chat
		// or cross-account status row can be stored (same pin the sequence-space table uses).
		builder
			.HasOne(e => e.Membership)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.AccountId, e.MembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.AccountId, m.Id })
			.HasConstraintName("FK_SyncLegacyMsgRecipientStates_ChatId_AccountId_MembershipId")
			.OnDelete(DeleteBehavior.Cascade);

		// Restrict: a status is evidence about a message, so it must not be able to outlive it silently
		// either way - the legacy row is deleted by nothing today.
		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => e.LegacyMessageId)
			.HasConstraintName("FK_SyncLegacyMsgRecipientStates_LegacyMessageId_Messages_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
