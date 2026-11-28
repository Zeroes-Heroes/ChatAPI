using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Keys;
using Services.Keys.Models;
using Services.Keys.Service;
using Xunit;

namespace KeyModuleTests;

/// <summary>
/// Proves the plan's central transactional requirement against a real PostgreSQL: "Protected key
/// rows share SAME PostgreSQL transaction with caller data using stage02 DbContext/model (no
/// separate hidden SaveChanges/transaction/commit)". <see cref="IProtectedMessageKeyStore.Stage"/>
/// only calls <c>DbSet.Add</c> - there is no SaveChanges inside the key module at all, so every test
/// here drives the one caller-owned <c>AppDbContext</c>/transaction itself.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TransactionIntegrationTests(DatabaseFixture fixture)
{
	private static byte[] RandomKey()
	{
		byte[] key = new byte[32];
		System.Security.Cryptography.RandomNumberGenerator.Fill(key);
		return key;
	}

	private static MessageKeyProvisioningService MakeService(TestMasterSecretDirectory dir, string version = "v1")
	{
		dir.WriteVersion(version);
		return new MessageKeyProvisioningService(new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor(version))));
	}

	[Fact]
	public async Task Commit_PersistsMessageAndProtectedKeyTogether_InOneSaveChanges()
	{
		using var dir = new TestMasterSecretDirectory();
		var service = MakeService(dir);
		var store = new ProtectedMessageKeyStore();

		await using AppDbContext db = fixture.CreateContext();
		var (_, chat, membership, message) = await TestData.CreateMessageChainAsync(db);

		byte[] rawKey = RandomKey();
		SyncMessageKeyEntity keyEntity = service.ProtectForStorage(message.Id, chat.Id, rawKey, DateTime.UtcNow);
		store.Stage(db, keyEntity);

		// Single SaveChangesAsync call: this is the "shares the caller's transaction" contract -
		// nothing in the key module called SaveChanges before this point.
		await db.SaveChangesAsync();

		await using AppDbContext verifyDb = fixture.CreateContext();
		SyncMessageKeyEntity? reloaded = await verifyDb.SyncMessageKeys.FirstOrDefaultAsync(k => k.MessageId == message.Id);
		Assert.NotNull(reloaded);
		Assert.Equal(chat.Id, reloaded!.ChatId);

		byte[] recovered = service.UnwrapForAuthorizedDelivery(reloaded, new MessageKeyAccessContext(message.Id, chat.Id));
		Assert.Equal(rawKey, recovered);
	}

	[Fact]
	public async Task ExplicitTransaction_RollbackLeavesNeitherMessageNorKeyPersisted()
	{
		using var dir = new TestMasterSecretDirectory();
		var service = MakeService(dir);
		var store = new ProtectedMessageKeyStore();

		await using AppDbContext db = fixture.CreateContext();
		var (_, chat, membership, _) = await TestData.CreateMessageChainAsync(db); // chat/membership persisted up front; message+key are the atomic unit under test

		string messageId = TestData.NewId("msg-rollback");
		await using var tx = await db.Database.BeginTransactionAsync();
		try
		{
			SyncMessageEntity message = new(
				messageId, chat.Id, membership.AccountId, membership.Id,
				messageSequence: 2, changeSequence: 2, Database.Enums.Sync.SyncOperationKind.SendText,
				authorKeyId: "author-key-1", authorKeyVersion: "1", signedPackage: "pkg", packageDigest: TestData.NewId("digest"),
				DateTime.UtcNow);
			db.SyncMessages.Add(message);

			SyncMessageKeyEntity keyEntity = service.ProtectForStorage(messageId, chat.Id, RandomKey(), DateTime.UtcNow);
			store.Stage(db, keyEntity);
			await db.SaveChangesAsync();

			throw new InvalidOperationException("simulated failure after staging both rows, before commit");
		}
		catch (InvalidOperationException)
		{
			await tx.RollbackAsync();
		}

		await using AppDbContext verifyDb = fixture.CreateContext();
		Assert.Null(await verifyDb.SyncMessages.FirstOrDefaultAsync(m => m.Id == messageId));
		Assert.Null(await verifyDb.SyncMessageKeys.FirstOrDefaultAsync(k => k.MessageId == messageId));
	}

	[Fact]
	public async Task ExplicitTransaction_CommitPersistsBothMessageAndKey()
	{
		using var dir = new TestMasterSecretDirectory();
		var service = MakeService(dir);
		var store = new ProtectedMessageKeyStore();

		await using AppDbContext db = fixture.CreateContext();
		var (_, chat, membership, _) = await TestData.CreateMessageChainAsync(db);

		string messageId = TestData.NewId("msg-commit");
		byte[] rawKey = RandomKey();
		await using var tx = await db.Database.BeginTransactionAsync();
		SyncMessageEntity message = new(
			messageId, chat.Id, membership.AccountId, membership.Id,
			messageSequence: 2, changeSequence: 2, Database.Enums.Sync.SyncOperationKind.SendText,
			authorKeyId: "author-key-1", authorKeyVersion: "1", signedPackage: "pkg", packageDigest: TestData.NewId("digest"),
			DateTime.UtcNow);
		db.SyncMessages.Add(message);
		SyncMessageKeyEntity keyEntity = service.ProtectForStorage(messageId, chat.Id, rawKey, DateTime.UtcNow);
		store.Stage(db, keyEntity);
		await db.SaveChangesAsync();
		await tx.CommitAsync();

		await using AppDbContext verifyDb = fixture.CreateContext();
		Assert.NotNull(await verifyDb.SyncMessages.FirstOrDefaultAsync(m => m.Id == messageId));
		SyncMessageKeyEntity? reloadedKey = await verifyDb.SyncMessageKeys.FirstOrDefaultAsync(k => k.MessageId == messageId);
		Assert.NotNull(reloadedKey);
		Assert.Equal(rawKey, service.UnwrapForAuthorizedDelivery(reloadedKey!, new MessageKeyAccessContext(messageId, chat.Id)));
	}

	[Fact]
	public async Task DeletingTheMessageCascadesToItsProtectedKeyRow()
	{
		using var dir = new TestMasterSecretDirectory();
		var service = MakeService(dir);
		var store = new ProtectedMessageKeyStore();

		await using AppDbContext db = fixture.CreateContext();
		var (_, chat, _, message) = await TestData.CreateMessageChainAsync(db);
		SyncMessageKeyEntity keyEntity = service.ProtectForStorage(message.Id, chat.Id, RandomKey(), DateTime.UtcNow);
		store.Stage(db, keyEntity);
		await db.SaveChangesAsync();

		await using AppDbContext deleteDb = fixture.CreateContext();
		SyncMessageEntity toDelete = await deleteDb.SyncMessages.FirstAsync(m => m.Id == message.Id);
		deleteDb.SyncMessages.Remove(toDelete);
		await deleteDb.SaveChangesAsync();

		await using AppDbContext verifyDb = fixture.CreateContext();
		Assert.Null(await verifyDb.SyncMessageKeys.FirstOrDefaultAsync(k => k.MessageId == message.Id));
	}

	[Fact]
	public async Task SchemaRejects_AProtectedKeyRowClaimingAChatIdThatDisagreesWithItsMessagesRealChat()
	{
		// Regression test for HIGH #5 (review-report.md): the DB previously had two independent FKs
		// (MessageId -> SyncMessages.Id, ChatId -> SyncChats.Id) with nothing tying them together, so
		// a row could claim ChatId = some other real chat's id while MessageId pointed to a message
		// that actually belongs to a *different* chat - silently corrupting the AES-GCM AAD's
		// owner-context/authorization scope. The composite FK (ChatId, MessageId) ->
		// SyncMessages(ChatId, Id) must reject that at the database level.
		await using AppDbContext db = fixture.CreateContext();
		var (_, chatA, membershipA, message) = await TestData.CreateMessageChainAsync(db); // message genuinely belongs to chatA
		SyncChatEntity chatB = await TestData.CreateChatAsync(db); // an unrelated, real, different chat

		await using AppDbContext writeDb = fixture.CreateContext();
		writeDb.SyncMessageKeys.Add(new SyncMessageKeyEntity(
			message.Id, chatB.Id, KeyProtectionFormatVersions.V1, KeyProtectionPurposes.MessageContentKey, "v1",
			new byte[16], new byte[12], new byte[16], DateTime.UtcNow));

		var ex = await Assert.ThrowsAsync<DbUpdateException>(() => writeDb.SaveChangesAsync());
		Assert.Null(await fixture.CreateContext().SyncMessageKeys.FirstOrDefaultAsync(k => k.MessageId == message.Id));
		_ = ex; // the specific PostgreSQL FK-violation message is asserted via direct SQL in the fix-report's scratch proof; here we only need the save to fail, not silently succeed.
	}

	[Fact]
	public async Task DbDumpAlone_WithoutTheMasterSecret_CannotRecoverTheRawKey()
	{
		// "A DB dump alone must not unlock keys." Simulate the dump by reading the raw row back with
		// nothing but PostgreSQL columns (no FileMasterSecretProvider instance in scope at all for
		// this part), then show that the only way to even attempt an unwrap requires constructing a
		// provider pointed at a real mounted secret - a dump has no such thing - and that guessing
		// (using a provider over the wrong key material) fails authentication rather than ever
		// producing the real raw key.
		using var dir = new TestMasterSecretDirectory();
		var service = MakeService(dir);
		var store = new ProtectedMessageKeyStore();

		await using AppDbContext db = fixture.CreateContext();
		var (_, chat, _, message) = await TestData.CreateMessageChainAsync(db);
		byte[] rawKey = RandomKey();
		SyncMessageKeyEntity keyEntity = service.ProtectForStorage(message.Id, chat.Id, rawKey, DateTime.UtcNow);
		store.Stage(db, keyEntity);
		await db.SaveChangesAsync();

		// The "dump": only the stored columns, read via a brand new context/connection.
		await using AppDbContext dumpDb = fixture.CreateContext();
		SyncMessageKeyEntity dumped = await dumpDb.SyncMessageKeys.FirstAsync(k => k.MessageId == message.Id);
		Assert.NotEqual(rawKey, dumped.ProtectedKey); // ciphertext bytes never equal the raw key bytes verbatim

		using var wrongSecretDir = new TestMasterSecretDirectory();
		wrongSecretDir.WriteVersion(dumped.KeyVersion); // an attacker's own guessed/unrelated 32-byte secret, not the real mounted one
		var attackerProtector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(wrongSecretDir.OptionsFor(dumped.KeyVersion)));
		var attackerService = new MessageKeyProvisioningService(attackerProtector);

		Assert.Throws<KeyProtectionIntegrityException>(() =>
			attackerService.UnwrapForAuthorizedDelivery(dumped, new MessageKeyAccessContext(message.Id, chat.Id)));
	}
}
