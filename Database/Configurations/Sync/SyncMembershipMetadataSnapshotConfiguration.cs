using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMembershipMetadataSnapshotConfiguration : IEntityTypeConfiguration<SyncMembershipMetadataSnapshotEntity>
{
	public void Configure(EntityTypeBuilder<SyncMembershipMetadataSnapshotEntity> builder)
	{
		builder.ToTable("SyncMembershipMetadataSnapshots");

		builder.HasKey(e => e.MembershipId);
		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.ChatName).HasMaxLength(200);

		builder.Property(e => e.ChatKindAtSnapshot).IsRequired();
		builder.Property(e => e.CapturedAtChangeSequence).IsRequired();
		builder.Property(e => e.CapturedAtUtc).IsRequired();

		builder
			.HasOne(e => e.Membership)
			.WithOne(e => e.MetadataSnapshot)
			.HasForeignKey<SyncMembershipMetadataSnapshotEntity>(e => e.MembershipId)
			.HasConstraintName("FK_SyncMembershipMetadataSnapshots_MembershipId_SyncMemberships_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
