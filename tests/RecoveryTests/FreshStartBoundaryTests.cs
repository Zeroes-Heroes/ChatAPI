using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Xunit;

namespace RecoveryTests;

/// <summary>
/// "Ново начало със същия номер": a new account context for the same phone, with the old context's
/// legacy chats out of reach at the surfaces that actually serve them.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class FreshStartBoundaryTests(DatabaseFixture fixture)
{
	private sealed class StubConnections(params LiveSessionConnection[] connections) : ISessionConnectionRegistry
	{
		public void Register(LiveSessionConnection connection) { }
		public void Unregister(string connectionId) { }
		public int AbortSession(string sessionId) => 0;
		public IReadOnlyCollection<LiveSessionConnection> Snapshot() => connections;
	}

	private static ClaimsPrincipal ContextPrincipal(string accountId) =>
		new(new ClaimsIdentity([new Claim(Services.Sessions.SessionClaimTypes.AccountContextId, accountId)], "test"));

	private static async Task<ChatEntity> CreateLegacyChatAsync(AppDbContext db, params UserEntity[] participants)
	{
		ChatEntity chat = new("legacy chat", participants);
		db.Chats.Add(chat);
		await db.SaveChangesAsync();
		return chat;
	}

	[Fact]
	public async Task NewContext_IsSeparate_AndTheOldContextKeepsEverything()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity old = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity oldDevice = await TestData.CreateTrustedDeviceAsync(db, old, spki, "key-1");

		SyncAccountEntity fresh = await ctx.Accounts.StartNewAccountContextAsync(db, old.LegacyUserId, "fresh-start-op-1", ctx.Clock.GetUtcNow().UtcDateTime);

		Assert.NotEqual(old.Id, fresh.Id);
		Assert.Equal(old.Id, fresh.PreviousAccountId);
		Assert.Null(fresh.EndedAtUtc);
		Assert.False(fresh.RevocationBarrierActive);

		await using AppDbContext verifyDb = fixture.CreateContext();
		SyncAccountEntity reloadedOld = await verifyDb.SyncAccounts.SingleAsync(a => a.Id == old.Id);
		Assert.NotNull(reloadedOld.EndedAtUtc);
		// Retained, never reassigned: the old context still owns its own devices.
		Assert.Equal(old.Id, (await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == oldDevice.Id)).AccountId);
		Assert.False(await verifyDb.SyncDeviceRegistrations.AnyAsync(d => d.AccountId == fresh.Id));
	}

	[Fact]
	public async Task TheNewContext_CannotSeeTheOldContextsLegacyChats()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity old = await TestData.CreateAccountAsync(db);
		UserEntity user = await db.Users.SingleAsync(u => u.Id == old.LegacyUserId);
		ChatEntity oldChat = await CreateLegacyChatAsync(db, user);

		SyncAccountEntity fresh = await ctx.Accounts.StartNewAccountContextAsync(db, old.LegacyUserId, "fresh-start-op-2", ctx.Clock.GetUtcNow().UtcDateTime);

		await using AppDbContext readDb = fixture.CreateContext();
		IAccountContextBoundary boundary = new AccountContextBoundary(readDb, new StubConnections());

		Assert.Contains(oldChat.Id, await boundary.ExcludedLegacyChatIdsAsync(ContextPrincipal(fresh.Id)));
		Assert.True(await boundary.IsLegacyChatExcludedAsync(ContextPrincipal(fresh.Id), oldChat.Id));
		// The old context itself is unaffected - its own data stays readable to it.
		Assert.Empty(await boundary.ExcludedLegacyChatIdsAsync(ContextPrincipal(old.Id)));
	}

	[Fact]
	public async Task AChatCreatedAfterTheFreshStart_BelongsToTheNewContext()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity old = await TestData.CreateAccountAsync(db);
		UserEntity user = await db.Users.SingleAsync(u => u.Id == old.LegacyUserId);
		SyncAccountEntity fresh = await ctx.Accounts.StartNewAccountContextAsync(db, old.LegacyUserId, "fresh-start-op-3", ctx.Clock.GetUtcNow().UtcDateTime);

		ChatEntity newChat = await CreateLegacyChatAsync(db, user);

		await using AppDbContext readDb = fixture.CreateContext();
		IAccountContextBoundary boundary = new AccountContextBoundary(readDb, new StubConnections());
		Assert.False(await boundary.IsLegacyChatExcludedAsync(ContextPrincipal(fresh.Id), newChat.Id));
	}

	[Fact]
	public async Task AnAccountThatNeverStartedOver_IsUnaffected()
	{
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		UserEntity user = await db.Users.SingleAsync(u => u.Id == account.LegacyUserId);
		ChatEntity chat = await CreateLegacyChatAsync(db, user);

		IAccountContextBoundary boundary = new AccountContextBoundary(db, new StubConnections());
		LegacyRecipients delivery = await boundary.ResolveLegacyRecipientsAsync([account.LegacyUserId], chat.Id);

		// Exactly the pre-stage-06 behaviour: plain user-id fan-out, no per-connection work at all.
		Assert.Equal([account.LegacyUserId], delivery.UserIds);
		Assert.Empty(delivery.ConnectionIds);
	}

	[Fact]
	public async Task LiveDelivery_ForAnExcludedChat_NeverReachesTheFreshContextsConnection()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity old = await TestData.CreateAccountAsync(db);
		UserEntity user = await db.Users.SingleAsync(u => u.Id == old.LegacyUserId);
		ChatEntity oldChat = await CreateLegacyChatAsync(db, user);
		SyncAccountEntity fresh = await ctx.Accounts.StartNewAccountContextAsync(db, old.LegacyUserId, "fresh-start-op-4", ctx.Clock.GetUtcNow().UtcDateTime);

		// Both contexts of the SAME phone have a live connection; SignalR's user-id fan-out cannot tell
		// them apart, which is exactly what this boundary exists for.
		LiveSessionConnection oldConnection = new("conn-old", old.Id, "sess-old", "dev-old", 1, DateTimeOffset.MaxValue, DateTimeOffset.UtcNow, () => { });
		LiveSessionConnection freshConnection = new("conn-fresh", fresh.Id, "sess-fresh", "dev-fresh", 1, DateTimeOffset.MaxValue, DateTimeOffset.UtcNow, () => { });

		await using AppDbContext readDb = fixture.CreateContext();
		IAccountContextBoundary boundary = new AccountContextBoundary(readDb, new StubConnections(oldConnection, freshConnection));
		LegacyRecipients delivery = await boundary.ResolveLegacyRecipientsAsync([old.LegacyUserId], oldChat.Id);

		Assert.Empty(delivery.UserIds);
		Assert.Equal(["conn-old"], delivery.ConnectionIds);
		Assert.DoesNotContain("conn-fresh", delivery.ConnectionIds);
	}

	/// <summary>
	/// Push carries the message text and the old chat id, and a device token is only known by legacy user
	/// id - so the fresh context's install would otherwise be notified of the old context's messages.
	/// </summary>
	[Fact]
	public async Task PushDelivery_ForAnExcludedChat_SkipsThePhoneThatStartedOver()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity old = await TestData.CreateAccountAsync(db);
		SyncAccountEntity other = await TestData.CreateAccountAsync(db);
		UserEntity user = await db.Users.SingleAsync(u => u.Id == old.LegacyUserId);
		UserEntity peer = await db.Users.SingleAsync(u => u.Id == other.LegacyUserId);
		ChatEntity oldChat = await CreateLegacyChatAsync(db, user, peer);
		await ctx.Accounts.StartNewAccountContextAsync(db, old.LegacyUserId, "fresh-start-op-5", ctx.Clock.GetUtcNow().UtcDateTime);

		await using AppDbContext readDb = fixture.CreateContext();
		IAccountContextBoundary boundary = new AccountContextBoundary(readDb, new StubConnections());

		// The peer is still in the old chat and unaffected; the phone that started over is dropped.
		Assert.Equal(
			[other.LegacyUserId],
			await boundary.NotifiableLegacyUserIdsAsync([old.LegacyUserId, other.LegacyUserId], oldChat.Id));

		// A chat created after the fresh start notifies everyone, exactly as before stage 06.
		ChatEntity newChat = await CreateLegacyChatAsync(db, user, peer);
		Assert.Equal(
			[old.LegacyUserId, other.LegacyUserId],
			await boundary.NotifiableLegacyUserIdsAsync([old.LegacyUserId, other.LegacyUserId], newChat.Id));
	}

	[Fact]
	public async Task AConcurrentOldContextWrite_CannotSlipPastTheExclusionSnapshot()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity old = await TestData.CreateAccountAsync(db);
		UserEntity user = await db.Users.SingleAsync(u => u.Id == old.LegacyUserId);
		var (spki, _) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity device = await TestData.CreateTrustedDeviceAsync(db, old, spki, "key-1");
		SyncLocalInstanceEntity instance = await TestData.CreateLocalInstanceAsync(db, device);
		SyncDeviceSessionEntity session = await TestData.CreateSessionAsync(db, old, device, instance, "raw-token-1");
		ClaimsPrincipal oldCaller = ctx.AppSessionPrincipal(old.LegacyUserId, old.Id, session.Id, device.Id, instance.Id, device.TrustVersion);

		// An in-flight legacy write of the OLD context, holding its device/session locks exactly like
		// TrustedMutation does, while the fresh start runs on another connection.
		await using AppDbContext writerDb = fixture.CreateContext();
		await using var writerTx = await writerDb.Database.BeginTransactionAsync();
		SessionTrustChecker writerTrust = new(writerDb, ctx.Clock);
		TrustCheckResult writerTrustResult = await writerTrust.CheckAndLockDeviceForMutationAsync(oldCaller);
		Assert.True(writerTrustResult.IsTrusted);

		Task<SyncAccountEntity> freshStart = Task.Run(async () =>
		{
			await using AppDbContext freshDb = fixture.CreateContext();
			return await ctx.Accounts.StartNewAccountContextAsync(freshDb, old.LegacyUserId, "fresh-start-op-6", ctx.Clock.GetUtcNow().UtcDateTime);
		});

		// The writer commits its chat first; the fresh start is still blocked on the device/session locks.
		ChatEntity racingChat = new("racing chat", [user]);
		writerDb.Chats.Add(racingChat);
		await writerDb.SaveChangesAsync();
		await writerTx.CommitAsync();

		SyncAccountEntity fresh = await freshStart;

		await using AppDbContext readDb = fixture.CreateContext();
		IAccountContextBoundary boundary = new AccountContextBoundary(readDb, new StubConnections());
		Assert.True(await boundary.IsLegacyChatExcludedAsync(ContextPrincipal(fresh.Id), racingChat.Id));
	}

	/// <summary>
	/// A lost response is indistinguishable from a failure on the device, so the retry must resume the
	/// successor the first attempt already created - even once that successor has been enrolled in and
	/// has data - instead of ending it and chaining a second restart.
	/// </summary>
	[Fact]
	public async Task RetryingTheSameOperation_ResumesTheSuccessor_EvenAfterItHasBeenUsed()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity old = await TestData.CreateAccountAsync(db);

		SyncAccountEntity first = await ctx.Accounts.StartNewAccountContextAsync(db, old.LegacyUserId, "op-retry", ctx.Clock.GetUtcNow().UtcDateTime);

		// The successor is in use by the time the retry arrives: a device and a session of its own.
		var (spki, _) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity successorDevice = await TestData.CreateTrustedDeviceAsync(db, first, spki, "key-successor");
		SyncLocalInstanceEntity successorInstance = await TestData.CreateLocalInstanceAsync(db, successorDevice);
		await TestData.CreateSessionAsync(db, first, successorDevice, successorInstance, "raw-token-successor");

		await using AppDbContext retryDb = fixture.CreateContext();
		SyncAccountEntity resumed = await ctx.Accounts.StartNewAccountContextAsync(retryDb, old.LegacyUserId, "op-retry", ctx.Clock.GetUtcNow().UtcDateTime);

		Assert.Equal(first.Id, resumed.Id);
		Assert.Equal(old.Id, resumed.PreviousAccountId);
		Assert.Null(resumed.EndedAtUtc);

		await using AppDbContext verifyDb = fixture.CreateContext();
		// Exactly two contexts for this phone, and the successor still owns its device/session.
		Assert.Equal(2, await verifyDb.SyncAccounts.CountAsync(a => a.LegacyUserId == old.LegacyUserId));
		Assert.Equal(first.Id, (await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == successorDevice.Id)).AccountId);
	}

	/// <summary>A genuinely new, user-requested fresh start carries a new operation id and really does start over; the superseded operation is never resurrected or disclosed.</summary>
	[Fact]
	public async Task ANewOperationStartsOver_AndTheSupersededOperationIsRefused()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity old = await TestData.CreateAccountAsync(db);

		SyncAccountEntity first = await ctx.Accounts.StartNewAccountContextAsync(db, old.LegacyUserId, "op-1", ctx.Clock.GetUtcNow().UtcDateTime);

		await using AppDbContext secondDb = fixture.CreateContext();
		SyncAccountEntity second = await ctx.Accounts.StartNewAccountContextAsync(secondDb, old.LegacyUserId, "op-2", ctx.Clock.GetUtcNow().UtcDateTime);
		Assert.NotEqual(first.Id, second.Id);
		Assert.Equal(first.Id, second.PreviousAccountId);

		// "op-1" now names an ENDED context: retrying it must not resurrect it, and must not end "op-2"'s.
		await using AppDbContext staleDb = fixture.CreateContext();
		await Assert.ThrowsAsync<Services.Sessions.SessionNotTrustedException>(() =>
			ctx.Accounts.StartNewAccountContextAsync(staleDb, old.LegacyUserId, "op-1", ctx.Clock.GetUtcNow().UtcDateTime));

		await using AppDbContext verifyDb = fixture.CreateContext();
		Assert.Null((await verifyDb.SyncAccounts.SingleAsync(a => a.Id == second.Id)).EndedAtUtc);
		Assert.Equal(3, await verifyDb.SyncAccounts.CountAsync(a => a.LegacyUserId == old.LegacyUserId));
	}

	/// <summary>Another identity's operation id is only ever its own: it names nothing here, so this phone simply starts over.</summary>
	[Fact]
	public async Task AnotherIdentitysOperationId_NeverResolvesToThatIdentitysContext()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity mine = await TestData.CreateAccountAsync(db);
		SyncAccountEntity theirs = await TestData.CreateAccountAsync(db);

		SyncAccountEntity theirSuccessor = await ctx.Accounts.StartNewAccountContextAsync(db, theirs.LegacyUserId, "shared-op", ctx.Clock.GetUtcNow().UtcDateTime);

		await using AppDbContext mineDb = fixture.CreateContext();
		SyncAccountEntity mySuccessor = await ctx.Accounts.StartNewAccountContextAsync(mineDb, mine.LegacyUserId, "shared-op", ctx.Clock.GetUtcNow().UtcDateTime);

		Assert.NotEqual(theirSuccessor.Id, mySuccessor.Id);
		Assert.Equal(mine.Id, mySuccessor.PreviousAccountId);
		Assert.Equal(mine.LegacyUserId, mySuccessor.LegacyUserId);
	}
}
