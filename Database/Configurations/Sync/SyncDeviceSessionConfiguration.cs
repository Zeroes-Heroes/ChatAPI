using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncDeviceSessionConfiguration : IEntityTypeConfiguration<SyncDeviceSessionEntity>
{
	public void Configure(EntityTypeBuilder<SyncDeviceSessionEntity> builder)
	{
		builder.ToTable("SyncDeviceSessions", tb =>
		{
			tb.HasCheckConstraint("CK_SyncDeviceSessions_RefreshHashRequiresExpiry",
				"(\"CurrentRefreshTokenHash\" IS NULL) = (\"RefreshTokenExpiresAtUtc\" IS NULL)");
			tb.HasCheckConstraint("CK_SyncDeviceSessions_EndedNotBeforeCreated", "\"EndedAtUtc\" IS NULL OR \"EndedAtUtc\" >= \"CreatedAtUtc\"");
		});

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);
		builder.Property(e => e.AccountId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.DeviceRegistrationId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.LocalInstanceId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.CreatedAtUtc).IsRequired();
		builder.Property(e => e.TrustVersionAtIssuance).IsRequired();
		builder.Property(e => e.CurrentRefreshTokenHash);
		builder.Property(e => e.RefreshTokenExpiresAtUtc).IsRequired(false);
		builder.Property(e => e.LastRotationOperationId).HasMaxLength(150);

		builder.HasAlternateKey(e => new { e.AccountId, e.Id }).HasName("AK_SyncDeviceSessions_AccountId_Id");

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncDeviceSessions_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the owning device registration must belong to the same account as the session.
		builder
			.HasOne(e => e.DeviceRegistration)
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.DeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncDeviceSessions_AccountId_DeviceRegistrationId_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the local instance must belong to this same device registration.
		builder
			.HasOne(e => e.LocalInstance)
			.WithMany()
			.HasForeignKey(e => new { e.DeviceRegistrationId, e.LocalInstanceId })
			.HasPrincipalKey(l => new { l.DeviceRegistrationId, l.Id })
			.HasConstraintName("FK_SyncDeviceSessions_DeviceRegistrationId_LocalInstanceId_SyncLocalInstances")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
