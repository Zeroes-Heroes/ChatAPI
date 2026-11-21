using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMembershipMessageStatusSnapshotConfiguration : IEntityTypeConfiguration<SyncMembershipMessageStatusSnapshotEntity>
{
	public void Configure(EntityTypeBuilder<SyncMembershipMessageStatusSnapshotEntity> builder)
	{
		builder.ToTable("SyncMembershipMessageStatusSnapshots");

		builder.HasKey(e => new { e.MembershipId, e.MessageId, e.RecipientAccountId });

		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.MessageId).HasMaxLength(100);
		builder.Property(e => e.RecipientAccountId).HasMaxLength(100);

		builder
			.HasOne(e => e.RecipientAccount)
			.WithMany()
			.HasForeignKey(e => e.RecipientAccountId)
			.HasConstraintName("FK_SyncMembershipMessageStatusSnapshots_RecipientAccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the message must belong to the same chat as the owning (frozen) membership.
		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.MessageId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncMembershipMessageStatusSnapshots_ChatId_MessageId_SyncMessages")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne(e => e.Membership)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.MembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncMembershipMessageStatusSnapshots_ChatId_MembershipId_SyncMemberships")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
