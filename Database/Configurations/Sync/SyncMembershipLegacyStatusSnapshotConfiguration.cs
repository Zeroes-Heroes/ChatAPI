using Database.Entities;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMembershipLegacyStatusSnapshotConfiguration : IEntityTypeConfiguration<SyncMembershipLegacyStatusSnapshotEntity>
{
	public void Configure(EntityTypeBuilder<SyncMembershipLegacyStatusSnapshotEntity> builder)
	{
		builder.ToTable("SyncMembershipLegacyStatusSnapshots", tb =>
			tb.HasCheckConstraint("CK_SyncMembershipLegacyStatusSnapshots_CapturedAtChangeSequence_Positive", "\"CapturedAtChangeSequence\" > 0"));

		// One row per (stint, message, recipient): the two legacy status rows (Received, Seen) of the
		// same pair are merged into this row's two timestamps, so the archive answers "what was this
		// person's status" without replaying a log.
		builder.HasKey(e => new { e.MembershipId, e.LegacyMessageId, e.RecipientLegacyUserId });

		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.CapturedAtChangeSequence).IsRequired();

		builder
			.HasOne(e => e.Membership)
			.WithMany(e => e.LegacyStatusSnapshots)
			.HasForeignKey(e => e.MembershipId)
			.HasConstraintName("FK_SyncMembershipLegacyStatusSnapshots_MembershipId_SyncMemberships_Id")
			.OnDelete(DeleteBehavior.Cascade);

		// Restrict, not Cascade: an archive must not be silently emptied by a legacy row disappearing -
		// the snapshot is the evidence of what the removed member was allowed to see.
		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => e.LegacyMessageId)
			.HasConstraintName("FK_SyncMembershipLegacyStatusSnapshots_LegacyMessageId_Messages_Id")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne(e => e.RecipientUser)
			.WithMany()
			.HasForeignKey(e => e.RecipientLegacyUserId)
			.HasConstraintName("FK_SyncMembershipLegacyStatusSnapshots_RecipientLegacyUserId_Users_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
