using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMembershipSharedHistoryWindowConfiguration : IEntityTypeConfiguration<SyncMembershipSharedHistoryWindowEntity>
{
	public void Configure(EntityTypeBuilder<SyncMembershipSharedHistoryWindowEntity> builder)
	{
		builder.ToTable("SyncMembershipSharedHistoryWindows", tb =>
		{
			// An empty or inverted window is not representable: a window that would end before it starts
			// means "this period shares nothing", which is stored as the ABSENCE of a row, never as an
			// impossible range (the same rule SyncMembershipHistoryGrants follows in sequence space).
			tb.HasCheckConstraint("CK_SyncMembershipSharedHistoryWindows_Range_Valid", "\"ThroughUtc\" >= \"FromUtc\"");
			tb.HasCheckConstraint("CK_SyncMembershipSharedHistoryWindows_GrantedAtChangeSequence_Positive", "\"GrantedAtChangeSequence\" > 0");
		});

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).UseIdentityAlwaysColumn();

		builder.Property(e => e.MembershipId).HasMaxLength(100);

		builder.Property(e => e.FromUtc).IsRequired();
		builder.Property(e => e.ThroughUtc).IsRequired();
		builder.Property(e => e.SelectedPeriod).IsRequired();
		builder.Property(e => e.GrantedAtChangeSequence).IsRequired();
		builder.Property(e => e.GrantedAtUtc).IsRequired();

		// The windows of one stint are disjoint by construction (they are cut out of the adder's own
		// disjoint windows), so their lower bounds are distinct - and a retried add that somehow reached
		// the insert twice cannot silently double a window.
		builder.HasIndex(e => new { e.MembershipId, e.FromUtc }).IsUnique();

		builder
			.HasOne(e => e.Membership)
			.WithMany(e => e.SharedHistoryWindows)
			.HasForeignKey(e => e.MembershipId)
			.HasConstraintName("FK_SyncMembershipSharedHistoryWindows_MembershipId_SyncMemberships_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
