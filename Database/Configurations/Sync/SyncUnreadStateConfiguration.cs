using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncUnreadStateConfiguration : IEntityTypeConfiguration<SyncUnreadStateEntity>
{
	public void Configure(EntityTypeBuilder<SyncUnreadStateEntity> builder)
	{
		builder.ToTable("SyncUnreadStates");

		builder.HasKey(e => e.MembershipId);
		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.OldestUnreadMessageId).HasMaxLength(100);
		builder.Property(e => e.LatestUnreadMessageId).HasMaxLength(100);

		builder.Property(e => e.Version).IsRequired();
		builder.Property(e => e.Count).IsRequired();

		builder
			.HasOne(e => e.Membership)
			.WithOne(e => e.Unread)
			.HasForeignKey<SyncUnreadStateEntity>(e => e.MembershipId)
			.HasConstraintName("FK_SyncUnreadStates_MembershipId_SyncMemberships_Id")
			.OnDelete(DeleteBehavior.Cascade);

		// Composite, in addition to the single-column FK above: pins this row's own ChatId to the
		// true chat of the owning membership stint (same "anonymous pin" pattern used by
		// SyncMembershipMetadataSnapshotMemberConfiguration), so ChatId cannot drift from the
		// membership it is denormalized from.
		builder
			.HasOne<SyncMembershipEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.MembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncUnreadStates_ChatId_MembershipId_SyncMemberships")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: an unread pointer can only reference a message that belongs to this row's own
		// chat, never a message from some other chat in the database.
		builder
			.HasOne(e => e.OldestUnreadMessage)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.OldestUnreadMessageId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncUnreadStates_ChatId_OldestUnreadMessageId_SyncMessages")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne(e => e.LatestUnreadMessage)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.LatestUnreadMessageId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncUnreadStates_ChatId_LatestUnreadMessageId_SyncMessages")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
