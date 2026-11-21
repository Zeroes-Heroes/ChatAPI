using Database.Entities;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncArchiveDeviceDeliveryConfiguration : IEntityTypeConfiguration<SyncArchiveDeviceDeliveryEntity>
{
	public void Configure(EntityTypeBuilder<SyncArchiveDeviceDeliveryEntity> builder)
	{
		builder.ToTable("SyncArchiveDeviceDeliveries");

		builder.HasKey(e => new { e.MembershipId, e.LocalInstanceId, e.LegacyMessageId });

		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.LocalInstanceId).HasMaxLength(100);
		builder.Property(e => e.StoredAtUtc).IsRequired();

		builder
			.HasOne(e => e.Membership)
			.WithMany(e => e.ArchiveDeviceDeliveries)
			.HasForeignKey(e => e.MembershipId)
			.HasConstraintName("FK_SyncArchiveDeviceDeliveries_MembershipId_SyncMemberships_Id")
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(e => e.LocalInstance)
			.WithMany()
			.HasForeignKey(e => e.LocalInstanceId)
			.HasConstraintName("FK_SyncArchiveDeviceDeliveries_LocalInstanceId_SyncLocalInstances_Id")
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => e.LegacyMessageId)
			.HasConstraintName("FK_SyncArchiveDeviceDeliveries_LegacyMessageId_Messages_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
