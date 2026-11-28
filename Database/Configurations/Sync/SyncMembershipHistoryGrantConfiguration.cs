using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMembershipHistoryGrantConfiguration : IEntityTypeConfiguration<SyncMembershipHistoryGrantEntity>
{
	public void Configure(EntityTypeBuilder<SyncMembershipHistoryGrantEntity> builder)
	{
		builder.ToTable("SyncMembershipHistoryGrants", tb =>
		{
			// Coherent with the model-wide "real sequences start at 1" convention (see
			// Database.Sequencing.SyncSequenceConventions) and with each other: a range cannot
			// end before it starts, and a frozen-at position is itself a real change sequence.
			tb.HasCheckConstraint("CK_SyncMembershipHistoryGrants_FromMessageSequence_Positive", "\"FromMessageSequence\" > 0");
			tb.HasCheckConstraint("CK_SyncMembershipHistoryGrants_ThroughMessageSequence_Valid", "\"ThroughMessageSequence\" IS NULL OR \"ThroughMessageSequence\" >= \"FromMessageSequence\"");
			tb.HasCheckConstraint("CK_SyncMembershipHistoryGrants_FrozenAtChangeSequence_Positive", "\"FrozenAtChangeSequence\" IS NULL OR \"FrozenAtChangeSequence\" > 0");
		});

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).UseIdentityAlwaysColumn();

		builder.Property(e => e.MembershipId).HasMaxLength(100);

		builder.Property(e => e.FromMessageSequence).IsRequired();

		builder.HasIndex(e => new { e.MembershipId, e.FromMessageSequence }).IsUnique();

		builder
			.HasOne(e => e.Membership)
			.WithMany(e => e.HistoryGrants)
			.HasForeignKey(e => e.MembershipId)
			.HasConstraintName("FK_SyncMembershipHistoryGrants_MembershipId_SyncMemberships_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
