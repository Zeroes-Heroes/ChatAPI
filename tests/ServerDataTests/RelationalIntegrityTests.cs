using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ServerDataTests;

/// <summary>
/// Proves that the composite foreign keys actually reject cross-chat/cross-account associations
/// at the database level - a bare "globally unique id" foreign key alone would not catch these.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RelationalIntegrityTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task RecipientState_WithMembershipFromDifferentChat_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		SyncAccountEntity sender = await TestData.CreateAccountAsync(db);

		SyncChatEntity chatA = await TestData.CreateChatAsync(db, name: "Chat A");
		SyncChatEntity chatB = await TestData.CreateChatAsync(db, name: "Chat B");

		SyncMembershipEntity senderMembershipInA = await TestData.CreateMembershipAsync(db, chatA, sender);
		SyncMessageEntity messageInA = await TestData.CreateMessageAsync(db, chatA, senderMembershipInA, messageSequence: 1, changeSequence: 1);

		// This membership genuinely belongs to the account, but to chat B, not chat A.
		SyncMembershipEntity membershipInB = await TestData.CreateMembershipAsync(db, chatB, account);

		db.SyncMessageRecipientStates.Add(new SyncMessageRecipientStateEntity(messageInA.Id, chatA.Id, membershipInB.Id, account.Id, version: 1));

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23503", ((PostgresException)exception.InnerException!).SqlState); // foreign_key_violation
	}

	[Fact]
	public async Task DeviceState_WithDeviceRegistrationFromDifferentAccount_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity owningAccount = await TestData.CreateAccountAsync(db);
		SyncAccountEntity otherAccount = await TestData.CreateAccountAsync(db);
		(_, SyncLocalInstanceEntity otherAccountInstance) = await TestData.CreateDeviceAsync(db, otherAccount);

		SyncChatEntity chat = await TestData.CreateChatAsync(db);
		SyncMembershipEntity membership = await TestData.CreateMembershipAsync(db, chat, owningAccount);
		SyncMessageEntity message = await TestData.CreateMessageAsync(db, chat, membership, messageSequence: 1, changeSequence: 1);

		// otherAccountInstance's device really belongs to otherAccount, not owningAccount.
		db.SyncMessageDeviceStates.Add(new SyncMessageDeviceStateEntity(
			message.Id, chat.Id, otherAccountInstance.DeviceRegistrationId, otherAccountInstance.Id,
			owningAccount.Id, membership.Id, version: 1));

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23503", ((PostgresException)exception.InnerException!).SqlState);
	}

	[Fact]
	public async Task Message_WithSenderMembershipFromDifferentChat_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity sender = await TestData.CreateAccountAsync(db);
		SyncChatEntity chatA = await TestData.CreateChatAsync(db, name: "Chat A");
		SyncChatEntity chatB = await TestData.CreateChatAsync(db, name: "Chat B");
		SyncMembershipEntity membershipInB = await TestData.CreateMembershipAsync(db, chatB, sender);

		SyncMessageEntity message = new(
			TestData.NewId("msg"), chatA.Id, sender.Id, membershipInB.Id,
			messageSequence: 1, changeSequence: 1, Database.Enums.Sync.SyncOperationKind.SendText,
			authorKeyId: "k", authorKeyVersion: "1", signedPackage: "pkg", packageDigest: TestData.NewId("digest"),
			DateTime.UtcNow);
		db.SyncMessages.Add(message);

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23503", ((PostgresException)exception.InnerException!).SqlState);
	}

	[Fact]
	public async Task Messages_WithDuplicateMessageSequenceInSameChat_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity sender = await TestData.CreateAccountAsync(db);
		SyncChatEntity chat = await TestData.CreateChatAsync(db);
		SyncMembershipEntity membership = await TestData.CreateMembershipAsync(db, chat, sender);

		await TestData.CreateMessageAsync(db, chat, membership, messageSequence: 1, changeSequence: 1);

		db.SyncMessages.Add(new SyncMessageEntity(
			TestData.NewId("msg"), chat.Id, sender.Id, membership.Id,
			messageSequence: 1, changeSequence: 2, Database.Enums.Sync.SyncOperationKind.SendText,
			authorKeyId: "k", authorKeyVersion: "1", signedPackage: "pkg", packageDigest: TestData.NewId("digest"),
			DateTime.UtcNow));

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23505", ((PostgresException)exception.InnerException!).SqlState); // unique_violation
	}

	/// <summary>Finding 1: real sequences start at 1; 0 is reserved for "nothing applied yet" and must never be a stored sequence value.</summary>
	[Fact]
	public async Task Message_WithZeroMessageSequence_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity sender = await TestData.CreateAccountAsync(db);
		SyncChatEntity chat = await TestData.CreateChatAsync(db);
		SyncMembershipEntity membership = await TestData.CreateMembershipAsync(db, chat, sender);

		db.SyncMessages.Add(new SyncMessageEntity(
			TestData.NewId("msg"), chat.Id, sender.Id, membership.Id,
			messageSequence: 0, changeSequence: 1, Database.Enums.Sync.SyncOperationKind.SendText,
			authorKeyId: "k", authorKeyVersion: "1", signedPackage: "pkg", packageDigest: TestData.NewId("digest"),
			DateTime.UtcNow));

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23514", ((PostgresException)exception.InnerException!).SqlState); // check_violation
	}

	/// <summary>Finding 2: a legacy user may have at most one *current* (EndedAtUtc == null) SyncAccount context, but an ended one may be retained alongside a fresh one.</summary>
	[Fact]
	public async Task Account_FreshContextAlongsideRetainedEndedContext_ForSameLegacyUser_Succeeds()
	{
		using AppDbContext db = fixture.CreateContext();
		UserEntity user = new($"Test {Guid.NewGuid():N}", $"+1555{Random.Shared.Next(1000000, 9999999)}");
		db.Users.Add(user);
		await db.SaveChangesAsync();

		DateTime createdAt = DateTime.UtcNow.AddDays(-30);
		SyncAccountEntity endedContext = new(TestData.NewId("acct"), user.Id, createdAt) { EndedAtUtc = createdAt.AddDays(1) };
		db.SyncAccounts.Add(endedContext);
		await db.SaveChangesAsync();

		SyncAccountEntity freshContext = new(TestData.NewId("acct"), user.Id, DateTime.UtcNow);
		db.SyncAccounts.Add(freshContext);
		await db.SaveChangesAsync();

		List<SyncAccountEntity> contextsForUser = await db.SyncAccounts.Where(a => a.LegacyUserId == user.Id).ToListAsync();
		Assert.Equal(2, contextsForUser.Count);
		Assert.Contains(contextsForUser, a => a.Id == endedContext.Id && a.EndedAtUtc != null);
		Assert.Contains(contextsForUser, a => a.Id == freshContext.Id && a.EndedAtUtc == null);
	}

	[Fact]
	public async Task Account_WithTwoCurrentContexts_ForSameLegacyUser_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		UserEntity user = new($"Test {Guid.NewGuid():N}", $"+1555{Random.Shared.Next(1000000, 9999999)}");
		db.Users.Add(user);
		await db.SaveChangesAsync();

		db.SyncAccounts.Add(new SyncAccountEntity(TestData.NewId("acct"), user.Id, DateTime.UtcNow));
		await db.SaveChangesAsync();

		// Neither has EndedAtUtc set - both are "current" for the same legacy user.
		db.SyncAccounts.Add(new SyncAccountEntity(TestData.NewId("acct"), user.Id, DateTime.UtcNow));

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23505", ((PostgresException)exception.InnerException!).SqlState);
	}

	[Fact]
	public async Task Account_WithEndedAtUtcBeforeCreatedAtUtc_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		UserEntity user = new($"Test {Guid.NewGuid():N}", $"+1555{Random.Shared.Next(1000000, 9999999)}");
		db.Users.Add(user);
		await db.SaveChangesAsync();

		DateTime createdAt = DateTime.UtcNow;
		db.SyncAccounts.Add(new SyncAccountEntity(TestData.NewId("acct"), user.Id, createdAt) { EndedAtUtc = createdAt.AddSeconds(-1) });

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23514", ((PostgresException)exception.InnerException!).SqlState); // check_violation
	}

	/// <summary>Finding 3: a SyncOperationResult's AccountId must match the actual sender of the message it points to, not just any message in the same chat.</summary>
	[Fact]
	public async Task OperationResult_WithAccountDifferentFromMessageSender_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity sender = await TestData.CreateAccountAsync(db);
		SyncAccountEntity otherAccount = await TestData.CreateAccountAsync(db);
		(SyncDeviceRegistrationEntity otherDevice, SyncLocalInstanceEntity otherInstance) = await TestData.CreateDeviceAsync(db, otherAccount);

		SyncChatEntity chat = await TestData.CreateChatAsync(db);
		SyncMembershipEntity senderMembership = await TestData.CreateMembershipAsync(db, chat, sender);
		SyncMessageEntity message = await TestData.CreateMessageAsync(db, chat, senderMembership, messageSequence: 1, changeSequence: 1);

		// otherAccount never sent this message - only `sender` did.
		db.SyncOperationResults.Add(new SyncOperationResultEntity(
			otherAccount.Id, otherDevice.Id, otherInstance.Id, TestData.NewId("op"),
			message.PackageDigest, Database.Enums.Sync.SyncOperationOutcome.Accepted, DateTime.UtcNow)
		{
			ChatId = chat.Id,
			MessageId = message.Id
		});

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23503", ((PostgresException)exception.InnerException!).SqlState);
	}

	/// <summary>Finding 5: an unread pointer can only reference a message from its own chat, not from an arbitrary chat in the database.</summary>
	[Fact]
	public async Task UnreadState_WithOldestUnreadMessageFromDifferentChat_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		SyncAccountEntity otherSender = await TestData.CreateAccountAsync(db);

		SyncChatEntity chatA = await TestData.CreateChatAsync(db, name: "Chat A");
		SyncChatEntity chatB = await TestData.CreateChatAsync(db, name: "Chat B");

		SyncMembershipEntity membershipInA = await TestData.CreateMembershipAsync(db, chatA, account);
		SyncMembershipEntity senderMembershipInB = await TestData.CreateMembershipAsync(db, chatB, otherSender);
		SyncMessageEntity messageInB = await TestData.CreateMessageAsync(db, chatB, senderMembershipInB, messageSequence: 1, changeSequence: 1);

		db.SyncUnreadStates.Add(new SyncUnreadStateEntity(membershipInA.Id, chatA.Id, version: 1, count: 1)
		{
			OldestUnreadMessageId = messageInB.Id // genuinely exists, but belongs to chat B, not chat A.
		});

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23503", ((PostgresException)exception.InnerException!).SqlState);
	}

	[Fact]
	public async Task UnreadState_WithLatestUnreadMessageFromDifferentChat_IsRejected()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		SyncAccountEntity otherSender = await TestData.CreateAccountAsync(db);

		SyncChatEntity chatA = await TestData.CreateChatAsync(db, name: "Chat A");
		SyncChatEntity chatB = await TestData.CreateChatAsync(db, name: "Chat B");

		SyncMembershipEntity membershipInA = await TestData.CreateMembershipAsync(db, chatA, account);
		SyncMembershipEntity senderMembershipInB = await TestData.CreateMembershipAsync(db, chatB, otherSender);
		SyncMessageEntity messageInB = await TestData.CreateMessageAsync(db, chatB, senderMembershipInB, messageSequence: 1, changeSequence: 1);

		db.SyncUnreadStates.Add(new SyncUnreadStateEntity(membershipInA.Id, chatA.Id, version: 1, count: 1)
		{
			LatestUnreadMessageId = messageInB.Id
		});

		DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.IsType<PostgresException>(exception.InnerException);
		Assert.Equal("23503", ((PostgresException)exception.InnerException!).SqlState);
	}
}
