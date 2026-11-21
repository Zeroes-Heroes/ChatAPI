using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMembershipMetadataSnapshotMemberConfiguration : IEntityTypeConfiguration<SyncMembershipMetadataSnapshotMemberEntity>
{
	public void Configure(EntityTypeBuilder<SyncMembershipMetadataSnapshotMemberEntity> builder)
	{
		builder.ToTable("SyncMembershipMetadataSnapshotMembers");

		builder.HasKey(e => new { e.MembershipId, e.ParticipantAccountId });

		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.ParticipantAccountId).HasMaxLength(100);
		builder.Property(e => e.ParticipantMembershipIdAtSnapshot).HasMaxLength(100);

		builder.Property(e => e.ParticipantStateAtSnapshot).IsRequired();

		builder
			.HasOne(e => e.MetadataSnapshot)
			.WithMany(e => e.Members)
			.HasForeignKey(e => e.MembershipId)
			.HasConstraintName("FK_SyncMembershipMetadataSnapshotMembers_MembershipId_SyncMembershipMetadataSnapshots_MembershipId")
			.OnDelete(DeleteBehavior.Cascade);

		// Composite: pins this row's own ChatId to the true chat of the owning membership stint.
		builder
			.HasOne<SyncMembershipEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.MembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncMembershipMetadataSnapshotMembers_ChatId_MembershipId_SyncMemberships")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne(e => e.ParticipantAccount)
			.WithMany()
			.HasForeignKey(e => e.ParticipantAccountId)
			.HasConstraintName("FK_SyncMembershipMetadataSnapshotMembers_ParticipantAccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the other participant's own membership stint must belong to the same chat as the
		// owning snapshot's membership (ChatId is denormalized onto this row for exactly this purpose).
		builder
			.HasOne(e => e.ParticipantMembership)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.ParticipantMembershipIdAtSnapshot })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncMembershipMetadataSnapshotMembers_ChatId_ParticipantMembershipId_SyncMemberships")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
