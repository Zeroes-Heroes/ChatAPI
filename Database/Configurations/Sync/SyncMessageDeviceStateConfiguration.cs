using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMessageDeviceStateConfiguration : IEntityTypeConfiguration<SyncMessageDeviceStateEntity>
{
	public void Configure(EntityTypeBuilder<SyncMessageDeviceStateEntity> builder)
	{
		builder.ToTable("SyncMessageDeviceStates");

		builder.HasKey(e => new { e.MessageId, e.DeviceRegistrationId, e.LocalInstanceId });

		builder.Property(e => e.MessageId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.DeviceRegistrationId).HasMaxLength(100);
		builder.Property(e => e.LocalInstanceId).HasMaxLength(100);
		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.MembershipId).HasMaxLength(100);

		builder.Property(e => e.Version).IsRequired();

		builder.HasIndex(e => new { e.ChatId, e.AccountId });

		// Composite: the message must belong to this row's chat.
		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.MessageId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncMessageDeviceStates_ChatId_MessageId_SyncMessages")
			.OnDelete(DeleteBehavior.Cascade);

		// Composite: the membership must belong to both this chat and this account at once.
		builder
			.HasOne(e => e.Membership)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.AccountId, e.MembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.AccountId, m.Id })
			.HasConstraintName("FK_SyncMessageDeviceStates_ChatId_AccountId_MembershipId_SyncMemberships")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the device registration must belong to this account.
		builder
			.HasOne(e => e.DeviceRegistration)
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.DeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncMessageDeviceStates_AccountId_DeviceRegistrationId_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the local instance must belong to this device registration.
		builder
			.HasOne(e => e.LocalInstance)
			.WithMany()
			.HasForeignKey(e => new { e.DeviceRegistrationId, e.LocalInstanceId })
			.HasPrincipalKey(l => new { l.DeviceRegistrationId, l.Id })
			.HasConstraintName("FK_SyncMessageDeviceStates_DeviceRegistrationId_LocalInstanceId_SyncLocalInstances")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
