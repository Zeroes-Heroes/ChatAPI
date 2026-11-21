namespace Database.Context;

using Database.Configurations;
using Database.Configurations.Sync;
using Database.Entities;
using Database.Entities.Configurations;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
	public void ApplyMigrations() => Database.Migrate();

	public DbSet<UserEntity> Users { get; set; }
	public DbSet<UserDeviceEntity> UserDevices { get; set; }
	public DbSet<FriendshipEntity> Friendships { get; set; }
	public DbSet<UserLoginCodeEntity> UserLoginCodes { get; set; }
	public DbSet<CountryCodesEntity> CountryCodes { get; set; }
	public DbSet<ChatEntity> Chats { get; set; }
	public DbSet<MessageEntity> Messages { get; set; }
	public DbSet<MessageStatusEntity> MessagesStatus { get; set; }
	public DbSet<DeviceNotificationConfigEntity> DeviceNotificationsConfig { get; set; }

	// Sync protocol persistence model (plan 02). Additive and independent of the legacy tables
	// above, apart from SyncAccountEntity bridging into UserEntity for auth binding.
	public DbSet<SyncAccountEntity> SyncAccounts { get; set; }
	public DbSet<SyncDeviceRegistrationEntity> SyncDeviceRegistrations { get; set; }
	public DbSet<SyncLocalInstanceEntity> SyncLocalInstances { get; set; }
	public DbSet<SyncAccountCatalogCounterEntity> SyncAccountCatalogCounters { get; set; }
	public DbSet<SyncChatEntity> SyncChats { get; set; }
	public DbSet<SyncChatSequenceCounterEntity> SyncChatSequenceCounters { get; set; }
	public DbSet<SyncMembershipEntity> SyncMemberships { get; set; }
	public DbSet<SyncMembershipHistoryGrantEntity> SyncMembershipHistoryGrants { get; set; }
	public DbSet<SyncMembershipMetadataSnapshotEntity> SyncMembershipMetadataSnapshots { get; set; }
	public DbSet<SyncMembershipMetadataSnapshotMemberEntity> SyncMembershipMetadataSnapshotMembers { get; set; }
	public DbSet<SyncMembershipMessageStatusSnapshotEntity> SyncMembershipMessageStatusSnapshots { get; set; }
	public DbSet<SyncMessageEntity> SyncMessages { get; set; }
	public DbSet<SyncMessageKeyEntity> SyncMessageKeys { get; set; }
	public DbSet<SyncOperationResultEntity> SyncOperationResults { get; set; }
	public DbSet<SyncOutboxEntity> SyncOutbox { get; set; }
	public DbSet<SyncMessageRecipientStateEntity> SyncMessageRecipientStates { get; set; }
	public DbSet<SyncMessageDeviceStateEntity> SyncMessageDeviceStates { get; set; }
	public DbSet<SyncUnreadStateEntity> SyncUnreadStates { get; set; }
	public DbSet<SyncChatChangeEntity> SyncChatChanges { get; set; }
	public DbSet<SyncCatalogEntryEntity> SyncCatalogEntries { get; set; }

	// Device/session/revocation persistence model (plan 05). Additive on top of plan 02's identity
	// anchors - see SyncDeviceRegistrationEntity/SyncAccountEntity's own doc comments for the columns
	// this stage adds to those two existing tables.
	public DbSet<SyncDeviceChallengeEntity> SyncDeviceChallenges { get; set; }
	public DbSet<SyncDeviceSessionEntity> SyncDeviceSessions { get; set; }
	public DbSet<SyncRefreshTokenRotationEntity> SyncRefreshTokenRotations { get; set; }

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.ApplyConfiguration(new UserConfiguration());
		modelBuilder.ApplyConfiguration(new UserDeviceConfiguration());
		modelBuilder.ApplyConfiguration(new UserLoginCodeConfiguration());
		modelBuilder.ApplyConfiguration(new FriendshipConfiguration());
		modelBuilder.ApplyConfiguration(new CountryCodesConfiguration());
		modelBuilder.ApplyConfiguration(new ChatEntityConfiguration());
		modelBuilder.ApplyConfiguration(new MessageStatusEntityConfiguration());
		modelBuilder.ApplyConfiguration(new DeviceNotificationEntityConfig());

		modelBuilder.ApplyConfiguration(new SyncAccountConfiguration());
		modelBuilder.ApplyConfiguration(new SyncDeviceRegistrationConfiguration());
		modelBuilder.ApplyConfiguration(new SyncLocalInstanceConfiguration());
		modelBuilder.ApplyConfiguration(new SyncAccountCatalogCounterConfiguration());
		modelBuilder.ApplyConfiguration(new SyncChatConfiguration());
		modelBuilder.ApplyConfiguration(new SyncChatSequenceCounterConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMembershipConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMembershipHistoryGrantConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMembershipMetadataSnapshotConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMembershipMetadataSnapshotMemberConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMembershipMessageStatusSnapshotConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMessageConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMessageKeyConfiguration());
		modelBuilder.ApplyConfiguration(new SyncOperationResultConfiguration());
		modelBuilder.ApplyConfiguration(new SyncOutboxConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMessageRecipientStateConfiguration());
		modelBuilder.ApplyConfiguration(new SyncMessageDeviceStateConfiguration());
		modelBuilder.ApplyConfiguration(new SyncUnreadStateConfiguration());
		modelBuilder.ApplyConfiguration(new SyncChatChangeConfiguration());
		modelBuilder.ApplyConfiguration(new SyncCatalogEntryConfiguration());

		modelBuilder.ApplyConfiguration(new SyncDeviceChallengeConfiguration());
		modelBuilder.ApplyConfiguration(new SyncDeviceSessionConfiguration());
		modelBuilder.ApplyConfiguration(new SyncRefreshTokenRotationConfiguration());
	}
}
