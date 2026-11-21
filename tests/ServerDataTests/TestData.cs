using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums.Sync;

namespace ServerDataTests;

/// <summary>Minimal seeding helpers shared by the test classes; not a general-purpose repository.</summary>
internal static class TestData
{
	public static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

	public static async Task<SyncAccountEntity> CreateAccountAsync(AppDbContext db, CancellationToken ct = default)
	{
		UserEntity user = new($"Test {Guid.NewGuid():N}", $"+1555{Random.Shared.Next(1000000, 9999999)}");
		db.Users.Add(user);
		await db.SaveChangesAsync(ct);

		SyncAccountEntity account = new(NewId("acct"), user.Id, DateTime.UtcNow);
		db.SyncAccounts.Add(account);
		db.SyncAccountCatalogCounters.Add(new SyncAccountCatalogCounterEntity(account.Id));
		await db.SaveChangesAsync(ct);
		return account;
	}

	public static async Task<(SyncDeviceRegistrationEntity Device, SyncLocalInstanceEntity Instance)> CreateDeviceAsync(
		AppDbContext db, SyncAccountEntity account, CancellationToken ct = default)
	{
		SyncDeviceRegistrationEntity device = new(NewId("dev"), account.Id, DateTime.UtcNow);
		db.SyncDeviceRegistrations.Add(device);
		await db.SaveChangesAsync(ct);

		SyncLocalInstanceEntity instance = new(NewId("inst"), device.Id, DateTime.UtcNow);
		db.SyncLocalInstances.Add(instance);
		await db.SaveChangesAsync(ct);
		return (device, instance);
	}

	public static async Task<SyncChatEntity> CreateChatAsync(
		AppDbContext db, SyncChatKind kind = SyncChatKind.Group, string? name = "Test chat", CancellationToken ct = default)
	{
		SyncChatEntity chat = new(NewId("chat"), kind, name, DateTime.UtcNow);
		db.SyncChats.Add(chat);
		db.SyncChatSequenceCounters.Add(new SyncChatSequenceCounterEntity(chat.Id));
		await db.SaveChangesAsync(ct);
		return chat;
	}

	public static async Task<SyncMembershipEntity> CreateMembershipAsync(
		AppDbContext db, SyncChatEntity chat, SyncAccountEntity account, long rightsVersion = 1, CancellationToken ct = default)
	{
		SyncMembershipEntity membership = new(NewId("mem"), chat.Id, account.Id, SyncMembershipState.Active, rightsVersion, DateTime.UtcNow);
		db.SyncMemberships.Add(membership);
		await db.SaveChangesAsync(ct);
		return membership;
	}

	public static async Task<SyncMessageEntity> CreateMessageAsync(
		AppDbContext db, SyncChatEntity chat, SyncMembershipEntity senderMembership,
		long messageSequence, long changeSequence, CancellationToken ct = default)
	{
		SyncMessageEntity message = new(
			NewId("msg"), chat.Id, senderMembership.AccountId, senderMembership.Id,
			messageSequence, changeSequence, SyncOperationKind.SendText,
			authorKeyId: "author-key-1", authorKeyVersion: "1",
			signedPackage: "signed-package-placeholder", packageDigest: NewId("digest"),
			DateTime.UtcNow);
		db.SyncMessages.Add(message);
		await db.SaveChangesAsync(ct);
		return message;
	}

	public static async Task<SyncUnreadStateEntity> CreateUnreadStateAsync(
		AppDbContext db, SyncMembershipEntity membership, long version = 1, long count = 0, CancellationToken ct = default)
	{
		SyncUnreadStateEntity unread = new(membership.Id, membership.ChatId, version, count);
		db.SyncUnreadStates.Add(unread);
		await db.SaveChangesAsync(ct);
		return unread;
	}
}
