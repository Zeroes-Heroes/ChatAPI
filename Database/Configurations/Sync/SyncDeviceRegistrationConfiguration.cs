using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncDeviceRegistrationConfiguration : IEntityTypeConfiguration<SyncDeviceRegistrationEntity>
{
	public void Configure(EntityTypeBuilder<SyncDeviceRegistrationEntity> builder)
	{
		builder.ToTable("SyncDeviceRegistrations");

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);
		builder.Property(e => e.AccountId).HasMaxLength(100);

		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Covering unique index so children can tie (AccountId, DeviceRegistrationId) with a composite FK.
		builder.HasAlternateKey(e => new { e.AccountId, e.Id }).HasName("AK_SyncDeviceRegistrations_AccountId_Id");

		builder
			.HasOne(e => e.Account)
			.WithMany(e => e.Devices)
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncDeviceRegistrations_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
