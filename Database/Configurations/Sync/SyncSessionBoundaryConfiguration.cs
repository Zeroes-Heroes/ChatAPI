using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncSessionBoundaryConfiguration : IEntityTypeConfiguration<SyncSessionBoundaryEntity>
{
	public void Configure(EntityTypeBuilder<SyncSessionBoundaryEntity> builder)
	{
		builder.ToTable("SyncSessionBoundaries", tb =>
		{
			tb.HasCheckConstraint("CK_SyncSessionBoundaries_Ordinal_NonNegative", "\"Ordinal\" >= 0");
			tb.HasCheckConstraint("CK_SyncSessionBoundaries_Through_NonNegative", "\"ThroughChangeSequence\" >= 0");
			// docs/sync-contract-v1.md fixes the priority set at "up to ten chats".
			tb.HasCheckConstraint("CK_SyncSessionBoundaries_PriorityRank_Range",
				"\"PriorityRank\" IS NULL OR (\"PriorityRank\" >= 1 AND \"PriorityRank\" <= 10)");
			// Neither an uploaded nor an acknowledged position may claim more than the fixed bound.
			tb.HasCheckConstraint("CK_SyncSessionBoundaries_Acknowledged_WithinBoundary",
				"\"AcknowledgedSequence\" IS NULL OR \"AcknowledgedSequence\" <= \"ThroughChangeSequence\"");
			tb.HasCheckConstraint("CK_SyncSessionBoundaries_AcknowledgedTime",
				"(\"AcknowledgedSequence\" IS NULL) = (\"AcknowledgedAtUtc\" IS NULL)");
		});

		builder.HasKey(e => new { e.SyncSessionId, e.Ordinal });

		builder.Property(e => e.SyncSessionId).HasMaxLength(100);
		builder.Property(e => e.ChatId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.MembershipId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.RightsVersion).IsRequired();
		builder.Property(e => e.ThroughChangeSequence).IsRequired();
		builder.Property(e => e.IsFrozen).IsRequired();

		// One stint appears at most once in a session's fixed set, which is what lets a submitted position
		// or an acknowledgement name a scope unambiguously.
		builder.HasIndex(e => new { e.SyncSessionId, e.ChatId, e.MembershipId })
			.IsUnique()
			.HasDatabaseName("IX_SyncSessionBoundaries_SyncSessionId_ChatId_MembershipId");

		builder
			.HasOne(e => e.Session)
			.WithMany(s => s.Boundaries)
			.HasForeignKey(e => e.SyncSessionId)
			.HasConstraintName("FK_SyncSessionBoundaries_SyncSessionId_SyncSessions_Id")
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(e => e.Chat)
			.WithMany()
			.HasForeignKey(e => e.ChatId)
			.HasConstraintName("FK_SyncSessionBoundaries_ChatId_SyncChats_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the stint must really belong to this boundary's chat.
		builder
			.HasOne(e => e.Membership)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.MembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncSessionBoundaries_ChatId_MembershipId_SyncMemberships")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
