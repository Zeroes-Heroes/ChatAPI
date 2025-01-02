using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations
{
    internal class PushNotificationEntityConfiguration : IEntityTypeConfiguration<PushNotificationEntity>
    {
        public void Configure(EntityTypeBuilder<PushNotificationEntity> builder)
        {
            builder.ToTable("PushNotifications");

            builder
                .HasOne(e => e.User)
                .WithMany(e => e.PushNotifications)
                .HasForeignKey(e => e.UserId)
                .HasPrincipalKey(e => e.Id)
                .HasConstraintName("FK_PushNotifications_UserId_Users_Id");

            builder
                .HasOne(e => e.UserDevice)
                .WithOne(e => e.PushNotification)
                .HasForeignKey<PushNotificationEntity>(e => e.DeviceId)
                .HasPrincipalKey<UserDeviceEntity>(e => e.Id)
                .HasConstraintName("FK_PushNotification_DeviceId_UserDevice_Id");

            builder
                .HasOne(e => e.OperationSystem)
                .WithMany(e => e.PushNotifications)
                .HasForeignKey(e => e.OS)
                .HasPrincipalKey(e => e.Id)
                .HasConstraintName("FK_PushNotifications_OS_OperationSystem_Id");
        }
    }
}