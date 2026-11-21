using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncSessionConfiguration : IEntityTypeConfiguration<SyncSessionEntity>
{
	public void Configure(EntityTypeBuilder<SyncSessionEntity> builder)
	{
		builder.ToTable("SyncSessions", tb =>
		{
			tb.HasCheckConstraint("CK_SyncSessions_CatalogThroughSequence_NonNegative", "\"CatalogThroughSequence\" >= 0");
			tb.HasCheckConstraint("CK_SyncSessions_RightsVersion_NonNegative", "\"RightsVersion\" >= 0");
			tb.HasCheckConstraint("CK_SyncSessions_BoundaryCount_NonNegative", "\"BoundaryCount\" >= 0");
			// An acknowledgement can never claim more than this session's own fixed catalog bound.
			tb.HasCheckConstraint("CK_SyncSessions_CatalogAcknowledged_WithinBoundary",
				"\"CatalogAcknowledgedSequence\" IS NULL OR \"CatalogAcknowledgedSequence\" <= \"CatalogThroughSequence\"");
			tb.HasCheckConstraint("CK_SyncSessions_ExpiresAfterCreated", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
		});

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);
		builder.Property(e => e.AccountId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.DeviceRegistrationId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.LocalInstanceId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.DeviceSessionId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.ConnectionId).IsRequired().HasMaxLength(200);
		builder.Property(e => e.SnapshotId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.Mode).IsRequired();
		builder.Property(e => e.RightsVersion).IsRequired();
		builder.Property(e => e.CatalogThroughSequence).IsRequired();
		builder.Property(e => e.BoundaryCount).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();
		builder.Property(e => e.ExpiresAtUtc).IsRequired();

		// The one lookup every page does: "is this session this exact device's, and is it still live".
		builder.HasIndex(e => new { e.AccountId, e.LocalInstanceId, e.CreatedAtUtc })
			.HasDatabaseName("IX_SyncSessions_AccountId_LocalInstanceId_CreatedAtUtc");

		// Reuse of the live session for a repeated handshake on the same connection, and the maintenance
		// sweep's "which sessions are still live" predicate.
		builder.HasIndex(e => new { e.DeviceSessionId, e.ConnectionId })
			.HasDatabaseName("IX_SyncSessions_DeviceSessionId_ConnectionId");

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncSessions_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the registration must belong to the same account as the session.
		builder
			.HasOne(e => e.DeviceRegistration)
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.DeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncSessions_AccountId_DeviceRegistrationId_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the local instance must belong to that same registration.
		builder
			.HasOne(e => e.LocalInstance)
			.WithMany()
			.HasForeignKey(e => new { e.DeviceRegistrationId, e.LocalInstanceId })
			.HasPrincipalKey(l => new { l.DeviceRegistrationId, l.Id })
			.HasConstraintName("FK_SyncSessions_DeviceRegistrationId_LocalInstanceId_SyncLocalInstances")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the device session must belong to the same account.
		builder
			.HasOne(e => e.DeviceSession)
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.DeviceSessionId })
			.HasPrincipalKey(s => new { s.AccountId, s.Id })
			.HasConstraintName("FK_SyncSessions_AccountId_DeviceSessionId_SyncDeviceSessions")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
