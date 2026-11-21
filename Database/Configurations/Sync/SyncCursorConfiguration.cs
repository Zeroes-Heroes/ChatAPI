using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncCursorConfiguration : IEntityTypeConfiguration<SyncCursorEntity>
{
	public void Configure(EntityTypeBuilder<SyncCursorEntity> builder)
	{
		builder.ToTable("SyncCursors", tb =>
		{
			tb.HasCheckConstraint("CK_SyncCursors_FromSequence_NonNegative", "\"FromSequence\" >= 0");
			tb.HasCheckConstraint("CK_SyncCursors_UpperBound_NonNegative", "\"UpperBoundSequence\" >= 0");
			// A chat cursor names both halves of its scope; a catalog/boundary cursor names neither.
			tb.HasCheckConstraint("CK_SyncCursors_ChatScope_Complete",
				"(\"ChatId\" IS NULL) = (\"MembershipId\" IS NULL)");
			// StreamKind 1 is Chat (Database.Enums.Sync.SyncStreamKind): the chat stream always has a
			// scope, the catalog stream never does - docs/sync-contract-v1.md: "Catalog StreamPosition
			// requires null ChatId/MembershipId; Chat requires both."
			tb.HasCheckConstraint("CK_SyncCursors_StreamKind_Scope",
				"(\"StreamKind\" = 1) = (\"ChatId\" IS NOT NULL)");
			// What was issued can never pass the fixed bound this cursor was minted against.
			tb.HasCheckConstraint("CK_SyncCursors_IssuedThrough_WithinBound",
				"\"IssuedThroughSequence\" IS NULL OR \"IssuedThroughSequence\" <= \"UpperBoundSequence\"");
			// A terminal page has no continuation, by construction.
			tb.HasCheckConstraint("CK_SyncCursors_Terminal_HasNoNext",
				"NOT \"IsTerminal\" OR \"NextCursorId\" IS NULL");
			tb.HasCheckConstraint("CK_SyncCursors_ExpiresAfterCreated", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
		});

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);
		builder.Property(e => e.SyncSessionId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.AccountId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.DeviceRegistrationId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.LocalInstanceId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.SnapshotId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.NextCursorId).HasMaxLength(100);
		builder.Property(e => e.Surface).IsRequired();
		builder.Property(e => e.StreamKind).IsRequired();
		builder.Property(e => e.RightsVersion).IsRequired();
		builder.Property(e => e.Forward).IsRequired();
		builder.Property(e => e.FromSequence).IsRequired();
		builder.Property(e => e.UpperBoundSequence).IsRequired();
		builder.Property(e => e.IsTerminal).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();
		builder.Property(e => e.ExpiresAtUtc).IsRequired();

		// Cursor lifetime follows the sync session: the sweep that ends a session removes its cursors with it.
		builder.HasIndex(e => new { e.SyncSessionId, e.ExpiresAtUtc })
			.HasDatabaseName("IX_SyncCursors_SyncSessionId_ExpiresAtUtc");

		builder
			.HasOne(e => e.Session)
			.WithMany()
			.HasForeignKey(e => e.SyncSessionId)
			.HasConstraintName("FK_SyncCursors_SyncSessionId_SyncSessions_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
