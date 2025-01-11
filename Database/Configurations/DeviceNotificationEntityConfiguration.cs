using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Entities.Configurations
{
    internal class DeviceNotificationEntityConfig : IEntityTypeConfiguration<DeviceNotificationConfigEntity>
    {
        public void Configure(EntityTypeBuilder<DeviceNotificationConfigEntity> builder)
        {
            builder.ToTable("DeviceNotifications");
            builder.HasKey(d => d.Id);

            builder.Property(d => d.UserId)
                .IsRequired();

            builder.Property(d => d.DeviceId)
                .IsRequired();

            builder.Property(d => d.OperatingSystem)
                .IsRequired();

            builder.Property(d => d.Token)
                .IsRequired();

            builder.Property(d => d.IsNotificationEnabled);

            builder.HasOne(d => d.User)
                .WithMany()
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("FK_DeviceNotificationConfig_Users_UserId")
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(d => d.UserDevice)
                .WithMany()
                .HasForeignKey(d => d.DeviceId)
                .HasConstraintName("FK_DeviceNotificationConfig_UserDevices_DeviceId")
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}