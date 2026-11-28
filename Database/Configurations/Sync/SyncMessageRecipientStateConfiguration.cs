using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMessageRecipientStateConfiguration : IEntityTypeConfiguration<SyncMessageRecipientStateEntity>
{
	public void Configure(EntityTypeBuilder<SyncMessageRecipientStateEntity> builder)
	{
		builder.ToTable("SyncMessageRecipientStates");

		builder.HasKey(e => new { e.MessageId, e.MembershipId });

		builder.Property(e => e.MessageId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.AccountId).HasMaxLength(100);

		builder.Property(e => e.Version).IsRequired();

		builder.HasIndex(e => new { e.ChatId, e.AccountId });

		// Composite: the message must belong to this row's chat.
		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.MessageId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncMessageRecipientStates_ChatId_MessageId_SyncMessages")
			.OnDelete(DeleteBehavior.Cascade);

		// Composite: the membership must belong to both this chat and this account at once.
		builder
			.HasOne(e => e.Membership)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.AccountId, e.MembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.AccountId, m.Id })
			.HasConstraintName("FK_SyncMessageRecipientStates_ChatId_AccountId_MembershipId_SyncMemberships")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
