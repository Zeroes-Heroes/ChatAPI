using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncOperationResultConfiguration : IEntityTypeConfiguration<SyncOperationResultEntity>
{
	public void Configure(EntityTypeBuilder<SyncOperationResultEntity> builder)
	{
		builder.ToTable("SyncOperationResults");

		builder.HasKey(e => new { e.AccountId, e.DeviceRegistrationId, e.LocalInstanceId, e.ClientOperationId });

		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.DeviceRegistrationId).HasMaxLength(100);
		builder.Property(e => e.LocalInstanceId).HasMaxLength(100);
		builder.Property(e => e.ClientOperationId).HasMaxLength(150);
		builder.Property(e => e.PackageDigest).HasMaxLength(100).IsRequired();
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.MessageId).HasMaxLength(100);

		builder.Property(e => e.Outcome).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncOperationResults_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the device registration must belong to this account.
		builder
			.HasOne(e => e.DeviceRegistration)
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.DeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncOperationResults_AccountId_DeviceRegistrationId_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the local instance must belong to this device registration.
		builder
			.HasOne(e => e.LocalInstance)
			.WithMany()
			.HasForeignKey(e => new { e.DeviceRegistrationId, e.LocalInstanceId })
			.HasPrincipalKey(l => new { l.DeviceRegistrationId, l.Id })
			.HasConstraintName("FK_SyncOperationResults_DeviceRegistrationId_LocalInstanceId_SyncLocalInstances")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: when both are set, the message must belong to the stated chat AND be sent by
		// this exact result's own AccountId - not just any message in that chat. Without the
		// SenderAccountId leg, a result row could be stamped with an AccountId that never sent the
		// message it points to. Postgres only enforces a multi-column FK when none of its columns
		// are null (MATCH SIMPLE), so a Rejected result with ChatId set but MessageId null is still
		// allowed; AccountId itself is always set (it's part of this table's own primary key).
		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.AccountId, e.MessageId })
			.HasPrincipalKey(m => new { m.ChatId, m.SenderAccountId, m.Id })
			.HasConstraintName("FK_SyncOperationResults_ChatId_AccountId_MessageId_SyncMessages")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
