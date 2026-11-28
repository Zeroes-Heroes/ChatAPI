using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums.Sync;

namespace KeyModuleTests;

/// <summary>Minimal seeding helpers for the FK chain a SyncMessageKeyEntity needs (account, chat, membership, message). Mirrors ServerDataTests/TestData.cs's shape; a separate copy so that inherited test project stays untouched.</summary>
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

	public static async Task<SyncChatEntity> CreateChatAsync(AppDbContext db, CancellationToken ct = default)
	{
		SyncChatEntity chat = new(NewId("chat"), SyncChatKind.Group, "Test chat", DateTime.UtcNow);
		db.SyncChats.Add(chat);
		db.SyncChatSequenceCounters.Add(new SyncChatSequenceCounterEntity(chat.Id));
		await db.SaveChangesAsync(ct);
		return chat;
	}

	public static async Task<SyncMembershipEntity> CreateMembershipAsync(AppDbContext db, SyncChatEntity chat, SyncAccountEntity account, CancellationToken ct = default)
	{
		SyncMembershipEntity membership = new(NewId("mem"), chat.Id, account.Id, SyncMembershipState.Active, 1, DateTime.UtcNow);
		db.SyncMemberships.Add(membership);
		await db.SaveChangesAsync(ct);
		return membership;
	}

	public static async Task<SyncMessageEntity> CreateMessageAsync(AppDbContext db, SyncChatEntity chat, SyncMembershipEntity senderMembership, CancellationToken ct = default)
	{
		SyncMessageEntity message = new(
			NewId("msg"), chat.Id, senderMembership.AccountId, senderMembership.Id,
			messageSequence: 1, changeSequence: 1, SyncOperationKind.SendText,
			authorKeyId: "author-key-1", authorKeyVersion: "1",
			signedPackage: "signed-package-placeholder", packageDigest: NewId("digest"),
			DateTime.UtcNow);
		db.SyncMessages.Add(message);
		await db.SaveChangesAsync(ct);
		return message;
	}

	/// <summary>Builds the full (account, chat, membership, message) chain in independent SaveChanges calls, for tests that only need an already-committed message to attach a protected key row to.</summary>
	public static async Task<(SyncAccountEntity Account, SyncChatEntity Chat, SyncMembershipEntity Membership, SyncMessageEntity Message)> CreateMessageChainAsync(AppDbContext db, CancellationToken ct = default)
	{
		SyncAccountEntity account = await CreateAccountAsync(db, ct);
		SyncChatEntity chat = await CreateChatAsync(db, ct);
		SyncMembershipEntity membership = await CreateMembershipAsync(db, chat, account, ct);
		SyncMessageEntity message = await CreateMessageAsync(db, chat, membership, ct);
		return (account, chat, membership, message);
	}
}
