using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums;
using Database.Enums.Sync;
using Microsoft.AspNetCore.SignalR;
using Services.Hubs;
using Services.Membership.Interface;
using Services.Membership.Service;
using Services.Sessions;
using Services.Sessions.Service;

namespace MembershipTests;

/// <summary>
/// Composes the REAL stage-07 services (membership writer, access authority, block directory) over the
/// REAL stage-05 trust checker and account provisioner. Only the SignalR hub context is a recording
/// sink - a live socket is the one thing a test process genuinely cannot have, and what is published is
/// asserted from that recording rather than assumed.
/// </summary>
internal sealed class MembershipTestContext(DatabaseFixture fixture, DateTimeOffset? now = null)
{
	public FakeTimeProvider Clock { get; } = new(now ?? DateTimeOffset.UtcNow);

	public RecordingHubContext Hub { get; } = new();

	public AppDbContext CreateContext() => fixture.CreateContext();

	public IChatAccessAuthority Authority(AppDbContext db) =>
		new ChatAccessAuthority(db, new AccountContextBoundary(db, new Services.Sessions.Service.SessionConnectionRegistry()));

	public IChatMembershipService Membership(AppDbContext db) =>
		new ChatMembershipService(
			db,
			new SyncAccountProvisioningService(),
			new PersonalBlockDirectory(db),
			new SessionTrustChecker(db, Clock),
			new AccountContextBoundary(db, new Services.Sessions.Service.SessionConnectionRegistry()),
			Hub,
			Clock);

	/// <summary>A legacy user plus its current account context and one trusted device/session, i.e. a caller that passes the real trust check.</summary>
	public async Task<TestMember> CreateMemberAsync(AppDbContext db)
	{
		UserEntity user = new($"Test {Guid.NewGuid():N}", $"+1555{Random.Shared.Next(1000000, 9999999)}");
		db.Users.Add(user);
		await db.SaveChangesAsync();

		SyncAccountEntity account = new($"acct-{Guid.NewGuid():N}", user.Id, Clock.GetUtcNow().UtcDateTime);
		db.SyncAccounts.Add(account);
		db.SyncAccountCatalogCounters.Add(new SyncAccountCatalogCounterEntity(account.Id));
		await db.SaveChangesAsync();

		using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		SyncDeviceRegistrationEntity device = new(
			$"dev-{Guid.NewGuid():N}", account.Id, Clock.GetUtcNow().UtcDateTime, "k1",
			key.ExportSubjectPublicKeyInfo(), isFirstDevice: true, approvedByDeviceRegistrationId: null)
		{
			TrustState = DeviceTrustState.Trusted,
		};
		db.SyncDeviceRegistrations.Add(device);
		await db.SaveChangesAsync();

		SyncLocalInstanceEntity instance = new($"inst-{Guid.NewGuid():N}", device.Id, Clock.GetUtcNow().UtcDateTime);
		db.SyncLocalInstances.Add(instance);
		await db.SaveChangesAsync();

		SyncDeviceSessionEntity session = new(
			$"sess-{Guid.NewGuid():N}", account.Id, device.Id, instance.Id, Clock.GetUtcNow().UtcDateTime, device.TrustVersion,
			SHA256.HashData(Encoding.ASCII.GetBytes($"refresh-{Guid.NewGuid():N}")), Clock.GetUtcNow().UtcDateTime.AddDays(30));
		db.SyncDeviceSessions.Add(session);
		await db.SaveChangesAsync();

		return new TestMember(user.Id, account, device, instance, session, Principal(user.Id, account.Id, session.Id, device.Id, instance.Id, device.TrustVersion));
	}

	/// <summary>The claims an app-session token carries, exactly as <see cref="SessionTrustChecker"/> reads them.</summary>
	private ClaimsPrincipal Principal(int legacyUserId, string accountId, string sessionId, string deviceId, string localInstanceId, long trustVersion) =>
		new(new ClaimsIdentity(
		[
			new Claim(ClaimTypes.NameIdentifier, legacyUserId.ToString()),
			new Claim(SessionClaimTypes.TokenPurpose, SessionClaimTypes.AppPurpose),
			new Claim(SessionClaimTypes.AccountContextId, accountId),
			new Claim(SessionClaimTypes.SessionId, sessionId),
			new Claim(SessionClaimTypes.DeviceRegistrationId, deviceId),
			new Claim(SessionClaimTypes.LocalInstanceId, localInstanceId),
			new Claim(SessionClaimTypes.TrustVersion, trustVersion.ToString()),
			new Claim("exp", Clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds().ToString()),
		], "test"));

	public static async Task<ChatEntity> CreateLegacyChatAsync(AppDbContext db, string? name, params int[] legacyUserIds)
	{
		List<UserEntity> users = [];
		foreach (int id in legacyUserIds)
		{
			users.Add(await db.Users.FirstAsync(u => u.Id == id));
		}

		ChatEntity chat = new(name, users);
		db.Chats.Add(chat);
		await db.SaveChangesAsync();
		return chat;
	}

	/// <summary>A Blocked relation as the legacy API can only produce it: the request's TARGET is the blocker.</summary>
	public static async Task BlockAsync(AppDbContext db, int blockerLegacyUserId, int blockedLegacyUserId)
	{
		FriendshipEntity friendship = new(blockedLegacyUserId, blockerLegacyUserId) { Status = FriendshipStatus.Blocked };
		db.Friendships.Add(friendship);
		await db.SaveChangesAsync();
	}

	public static async Task BefriendAsync(AppDbContext db, int senderLegacyUserId, int targetLegacyUserId)
	{
		FriendshipEntity friendship = new(senderLegacyUserId, targetLegacyUserId) { Status = FriendshipStatus.Accepted };
		db.Friendships.Add(friendship);
		await db.SaveChangesAsync();
	}
}

internal sealed record TestMember(
	int LegacyUserId,
	SyncAccountEntity Account,
	SyncDeviceRegistrationEntity Device,
	SyncLocalInstanceEntity Instance,
	SyncDeviceSessionEntity Session,
	ClaimsPrincipal Principal);

/// <summary>One recorded live publication: the event name, its payload and the audience it was addressed to.</summary>
internal sealed record SentEvent(string Event, object? Payload, IReadOnlyList<string> Audience);

/// <summary>Records what the membership service publishes, per event name and audience.</summary>
internal sealed class RecordingHubContext : IHubContext<BaseHub>
{
	public List<SentEvent> Sent { get; } = [];

	public IHubClients Clients => new RecordingClients(Sent);

	public IGroupManager Groups => throw new NotSupportedException();

	private sealed class RecordingClients(List<SentEvent> sent) : IHubClients
	{
		public IClientProxy All => new RecordingProxy(sent, []);
		public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingProxy(sent, []);
		public IClientProxy Client(string connectionId) => new RecordingProxy(sent, [connectionId]);
		public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingProxy(sent, connectionIds);
		public IClientProxy Group(string groupName) => new RecordingProxy(sent, [groupName]);
		public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingProxy(sent, []);
		public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingProxy(sent, groupNames);
		public IClientProxy User(string userId) => new RecordingProxy(sent, [userId]);
		public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingProxy(sent, userIds);
	}

	private sealed class RecordingProxy(List<SentEvent> sent, IReadOnlyList<string> audience) : IClientProxy
	{
		public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
		{
			sent.Add(new SentEvent(method, args.Length > 0 ? args[0] : null, audience));
			return Task.CompletedTask;
		}
	}
}
