using Database.Entities;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncLegacyMessageDeviceStateConfiguration : IEntityTypeConfiguration<SyncLegacyMessageDeviceStateEntity>
{
	public void Configure(EntityTypeBuilder<SyncLegacyMessageDeviceStateEntity> builder)
	{
		builder.ToTable("SyncLegacyMessageDeviceStates");

		// One row per (SQLite copy, message). Not per device registration: clearing local data creates a
		// new instance that genuinely has to download the message again.
		builder.HasKey(e => new { e.LocalInstanceId, e.LegacyMessageId });

		builder.Property(e => e.LocalInstanceId).HasMaxLength(100);
		builder.Property(e => e.DeviceRegistrationId).HasMaxLength(100);
		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.MembershipId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.Version).IsRequired();

		// The composite FK's own columns; its leading columns also answer "which of this chat's messages
		// does this person already have device evidence for". Named explicitly: the conventional names
		// of both this and the registration index would exceed PostgreSQL's 63-character limit.
		builder.HasIndex(e => new { e.ChatId, e.AccountId, e.MembershipId })
			.HasDatabaseName("IX_SyncLegacyMsgDeviceStates_ChatId_AccountId_MembershipId");

		builder.HasIndex(e => new { e.AccountId, e.DeviceRegistrationId })
			.HasDatabaseName("IX_SyncLegacyMsgDeviceStates_AccountId_DeviceRegistrationId");

		// Declared rather than left to EF's ForeignKeyIndexConvention, which would add it to the runtime
		// model under a 69-character conventional name: the primary key leads with LocalInstanceId, so no
		// existing index covers this FK, and an undeclared one would make the hand-maintained migration
		// and model snapshot describe a different model than the context builds.
		builder.HasIndex(e => new { e.DeviceRegistrationId, e.LocalInstanceId })
			.HasDatabaseName("IX_SyncLegacyMsgDeviceStates_DeviceRegistrationId_InstanceId");

		// Composite: the stint must belong to both this chat and this account at once.
		builder
			.HasOne(e => e.Membership)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.AccountId, e.MembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.AccountId, m.Id })
			.HasConstraintName("FK_SyncLegacyMsgDeviceStates_ChatId_AccountId_MembershipId")
			.OnDelete(DeleteBehavior.Cascade);

		// Composite: the device registration must belong to the same account as the stint, so a device
		// of one account can never record progress against another account's membership.
		builder
			.HasOne<SyncDeviceRegistrationEntity>()
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.DeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncLegacyMsgDeviceStates_AccountId_DeviceRegistrationId")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the local instance must belong to that same device registration.
		builder
			.HasOne(e => e.LocalInstance)
			.WithMany()
			.HasForeignKey(e => new { e.DeviceRegistrationId, e.LocalInstanceId })
			.HasPrincipalKey(l => new { l.DeviceRegistrationId, l.Id })
			.HasConstraintName("FK_SyncLegacyMsgDeviceStates_DeviceRegistrationId_InstanceId")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => e.LegacyMessageId)
			.HasConstraintName("FK_SyncLegacyMsgDeviceStates_LegacyMessageId_Messages_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
