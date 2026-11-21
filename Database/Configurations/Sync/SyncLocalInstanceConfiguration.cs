using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncLocalInstanceConfiguration : IEntityTypeConfiguration<SyncLocalInstanceEntity>
{
	public void Configure(EntityTypeBuilder<SyncLocalInstanceEntity> builder)
	{
		builder.ToTable("SyncLocalInstances");

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);
		builder.Property(e => e.DeviceRegistrationId).HasMaxLength(100);

		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Covering unique index so children can tie (DeviceRegistrationId, LocalInstanceId) with a composite FK.
		builder.HasAlternateKey(e => new { e.DeviceRegistrationId, e.Id }).HasName("AK_SyncLocalInstances_DeviceRegistrationId_Id");

		builder
			.HasOne(e => e.DeviceRegistration)
			.WithMany(e => e.LocalInstances)
			.HasForeignKey(e => e.DeviceRegistrationId)
			.HasConstraintName("FK_SyncLocalInstances_DeviceRegistrationId_SyncDeviceRegistrations_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
