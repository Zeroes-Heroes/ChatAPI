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
using Services.History.Interface;
using Services.History.Service;
using Services.Hubs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Services.Authorship.Interface;
using Services.Authorship.Service;
using Services.Contacts.Interface;
using Services.Contacts.Service;
using Services.Crypto.Service;
using Services.Delivery;
using Services.Delivery.Interface;
using Services.Delivery.Service;
using Services.Keys.Interface;
using Services.Keys.Service;
using Services.Membership.Interface;
using Services.Membership.Service;
using Services.Messaging.Interface;
using Services.Messaging.Service;
using Services.NotificationDispatch.Interface;
using Services.Sessions;
using Services.Sessions.Service;
using Services.Statuses.Interface;
using Services.Statuses.Service;
using Services.Sync.Interface;
using Services.Sync.Service;

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

	/// <summary>Stage 09's personal-relation reader, over the same real tables the production wiring uses.</summary>
	public static IPersonalRelationDirectory Relations(AppDbContext db) => new PersonalRelationDirectory(db);

	/// <summary>Stage 09's one profile resolver.</summary>
	public static IProfileVisibility Profiles(AppDbContext db) => new ProfileVisibilityService(db, Relations(db));

	public IChatAccessAuthority Authority(AppDbContext db) =>
		new ChatAccessAuthority(db, new AccountContextBoundary(db, new Services.Sessions.Service.SessionConnectionRegistry()), Relations(db));

	public IChatMembershipService Membership(AppDbContext db) =>
		new ChatMembershipService(
			db,
			new SyncAccountProvisioningService(),
			new PersonalBlockDirectory(Relations(db)),
			new SessionTrustChecker(db, Clock),
			new AccountContextBoundary(db, new Services.Sessions.Service.SessionConnectionRegistry()),
			Authority(db),
			Statuses(db),
			Hub,
			Clock);

	/// <summary>Stage 08's history/archive reader, over the same real access check the production wiring uses.</summary>
	public IChatHistoryService History(AppDbContext db) =>
		new ChatHistoryService(db, Authority(db), new MessageAuthorshipAuthority(), Statuses(db), Clock);

	/// <summary>
	/// Stage 10's one owner of recipient/device statuses and unread, over the REAL access authority and
	/// the REAL trust checker - the same objects the hub resolves, so what these tests exercise is the
	/// production decision path, not a stand-in.
	/// </summary>
	/// <param name="config">
	/// The delivery bounds this writer queues status outbox rows under. Defaults to
	/// <see cref="DeliveryConfiguration"/>, which has the status-delivery gate ON - a test that wants the
	/// default-off deployment passes its own.
	/// </param>
	public IMessageStatusService Statuses(AppDbContext db, DeliveryOptions? config = null) =>
		new MessageStatusService(
			db, Authority(db), new SessionTrustChecker(db, Clock),
			Options.Create(config ?? DeliveryConfiguration()), Clock);

	/// <summary>
	/// Stage 09's relation writer, over the same real trust checker every other mutation uses. The push
	/// dispatcher is a no-op sink: a real one would send to Apple/Firebase, which a test process must not
	/// do, and nothing in these tests asserts on it.
	/// </summary>
	public IPersonalRelationService PersonalRelations(AppDbContext db) =>
		new PersonalRelationService(
			db,
			Relations(db),
			Profiles(db),
			new SessionTrustChecker(db, Clock),
			Hub,
			new NoOpNotificationDispatch(),
			NullLogger<PersonalRelationService>.Instance,
			Clock);

	/// <summary>Stage 11's author-key registry, over the REAL access authority, membership writer and trust checker.</summary>
	public IAuthorKeyRegistry AuthorKeys(AppDbContext db) =>
		new AuthorKeyRegistry(
			db, Authority(db), Membership(db),
			new AccountContextBoundary(db, new Services.Sessions.Service.SessionConnectionRegistry()),
			new SessionTrustChecker(db, Clock), Clock);

	/// <summary>
	/// Stage 11's acceptance, composed exactly as the production wiring composes it: the real JWS
	/// verifier, the real author-key registry, the real access authority and membership writer, the real
	/// key module and the real status service. The only substitution is the master secret, which in
	/// production is a read-only mounted file (<see cref="Services.Keys.Service.FileMasterSecretProvider"/>,
	/// covered by its own tests) and which a test process must not depend on the filesystem for.
	/// </summary>
	public IMessageAcceptanceService Acceptance(AppDbContext db) =>
		new MessageAcceptanceService(
			db,
			new MessagePackageVerifier(),
			AuthorKeys(db),
			Authority(db),
			Membership(db),
			new MessageKeyProvisioningService(new AesGcmMessageKeyProtector(new FixedMasterSecretProvider())),
			new ProtectedMessageKeyStore(),
			Statuses(db),
			new AccountContextBoundary(db, new Services.Sessions.Service.SessionConnectionRegistry()),
			new SessionTrustChecker(db, Clock),
			Clock);

	/// <summary>Stage 12's bounds, with a small attempt limit so exhausting it is observable.</summary>
	/// <param name="publishStatusChanges">
	/// The deployment gate behind v1 status delivery. ON by default here, because these tests exercise the
	/// fully deployed state; production defaults it OFF until every consumer understands
	/// <c>SyncOutbox.ChangeKind</c>.
	/// </param>
	public static DeliveryOptions DeliveryConfiguration(bool publishStatusChanges = true) => new()
	{
		ConnectionUri = "amqp://test-only/",
		ClaimBatchSize = 100,
		LeaseSeconds = 60,
		RetryBaseSeconds = 2,
		RetryMaxSeconds = 300,
		MaxAttempts = 3,
		PublishStatusChanges = publishStatusChanges,
	};

	/// <summary>Stage 12's outbox queue over the REAL table, with this context's controllable clock - which is what lets a lease expiry and a backoff be observed rather than waited for.</summary>
	public IOutboxStore Outbox(AppDbContext db, DeliveryOptions? config = null) =>
		new OutboxStore(db, Options.Create(config ?? DeliveryConfiguration()), Clock);

	/// <summary>
	/// Stage 12's live delivery, composed exactly as the production wiring composes it: the real access
	/// authority (rights and windows), the real author-key registry, the real key module and the real
	/// protected-key store. Only the SignalR hub context is the recording sink every other suite uses.
	/// </summary>
	public ILiveDeliveryService Delivery(AppDbContext db) =>
		new LiveDeliveryService(
			db,
			Authority(db),
			AuthorKeys(db),
			new MessageKeyProvisioningService(new AesGcmMessageKeyProtector(new FixedMasterSecretProvider())),
			new ProtectedMessageKeyStore(),
			// The same projection the HTTP pages answer from: a live status event and a catch-up page have
			// to describe the same row identically, including its EventId.
			Projection(db),
			Hub,
			NullLogger<LiveDeliveryService>.Instance);

	/// <summary>
	/// Stage 13's sync module, composed exactly as the production wiring composes it: the real cursor
	/// store, the real trust checker, the real access authority, the real author-key registry and the
	/// real key module. Nothing here is a stand-in - a page these tests read is the page the controller
	/// and the hub would answer.
	/// </summary>
	public static ISyncCursorStore Cursors(AppDbContext db) => new SyncCursorStore(db);

	public SyncProjection Projection(AppDbContext db) =>
		new(db,
			AuthorKeys(db),
			new MessageKeyProvisioningService(new AesGcmMessageKeyProtector(new FixedMasterSecretProvider())),
			new ProtectedMessageKeyStore(),
			NullLogger<SyncProjection>.Instance);

	public ISyncSessionService SyncSessions(AppDbContext db) =>
		new SyncSessionService(db, new SessionTrustChecker(db, Clock), Cursors(db), Clock);

	public ISyncPageService SyncPages(AppDbContext db) =>
		new SyncPageService(db, Authority(db), Cursors(db), Projection(db), Clock, NullLogger<SyncPageService>.Instance);

	public ISyncRetention SyncRetention(AppDbContext db) =>
		new SyncRetentionService(db, Clock, NullLogger<SyncRetentionService>.Instance);

	/// <summary>A legacy message, the only kind that exists today (SyncMessages start in stage 11).</summary>
	public static async Task<MessageEntity> SendLegacyAsync(AppDbContext db, ChatEntity chat, int senderLegacyUserId, string content, DateTime atUtc)
	{
		MessageEntity message = new(content, senderLegacyUserId, chat.Id, atUtc);
		db.Messages.Add(message);
		await db.SaveChangesAsync();
		return message;
	}

	/// <summary>A public status row exactly as the legacy hub writes it (the table is a log: one row per reached status).</summary>
	public static async Task MarkLegacyStatusAsync(AppDbContext db, int messageId, int receiverLegacyUserId, MessageStatus status, DateTime atUtc)
	{
		db.MessagesStatus.Add(new MessageStatusEntity(messageId, receiverLegacyUserId, status, atUtc));
		await db.SaveChangesAsync();
	}

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

	/// <summary>
	/// A SECOND authorized device of an existing account: its own registration, its own SQLite copy
	/// (LocalInstanceId) and its own session. Stage 08 needs it because restoring an archive is scoped to
	/// one local instance, not to the person - the public marks already say the person received it.
	/// </summary>
	public async Task<TestMember> AddDeviceAsync(AppDbContext db, TestMember owner)
	{
		using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		SyncDeviceRegistrationEntity device = new(
			$"dev-{Guid.NewGuid():N}", owner.Account.Id, Clock.GetUtcNow().UtcDateTime, "k1",
			key.ExportSubjectPublicKeyInfo(), isFirstDevice: false, approvedByDeviceRegistrationId: owner.Device.Id)
		{
			TrustState = DeviceTrustState.Trusted,
		};
		db.SyncDeviceRegistrations.Add(device);
		await db.SaveChangesAsync();

		SyncLocalInstanceEntity instance = new($"inst-{Guid.NewGuid():N}", device.Id, Clock.GetUtcNow().UtcDateTime);
		db.SyncLocalInstances.Add(instance);
		await db.SaveChangesAsync();

		SyncDeviceSessionEntity session = new(
			$"sess-{Guid.NewGuid():N}", owner.Account.Id, device.Id, instance.Id, Clock.GetUtcNow().UtcDateTime, device.TrustVersion,
			SHA256.HashData(Encoding.ASCII.GetBytes($"refresh-{Guid.NewGuid():N}")), Clock.GetUtcNow().UtcDateTime.AddDays(30));
		db.SyncDeviceSessions.Add(session);
		await db.SaveChangesAsync();

		return new TestMember(
			owner.LegacyUserId, owner.Account, device, instance, session,
			Principal(owner.LegacyUserId, owner.Account.Id, session.Id, device.Id, instance.Id, device.TrustVersion));
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

/// <summary>
/// The one genuinely unavailable seam for the relation writer: push delivery leaves the process. Every
/// other collaborator above is the real service over the real database.
/// </summary>
internal sealed class NoOpNotificationDispatch : INotificationDispatch
{
	public Task NotificationForNewMessage(int[] receiversIds, int senderUserId, string message, int chatId) => Task.CompletedTask;
	public Task NotificationForNewChat(int[] chatParticipantIds, int chatCreatorId, int chatId) => Task.CompletedTask;
	public Task NotificationForNewFriendshipRequest(int receiverUserId, string name) => Task.CompletedTask;
	public Task NotificationAcceptFriendship(int notificationRecipientId, int requestSenderId) => Task.CompletedTask;
	public Task NotificationForRejectedFriendship(int notificationRecipientId, int requestSenderId) => Task.CompletedTask;
	public Task NotificationForBlockedFriendship(int notificationRecipientId, int requestSenderId) => Task.CompletedTask;
}

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

/// <summary>
/// The one genuinely unavailable seam for the key module in a test process: in production the master
/// secret is a read-only mounted FILE (<see cref="Services.Keys.Service.FileMasterSecretProvider"/>,
/// which has its own tests), never a constant. The protection algorithm, its AAD binding and the
/// storage format exercised through this provider are the real ones.
/// </summary>
internal sealed class FixedMasterSecretProvider : IMasterSecretProvider
{
	private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3))];

	public string CurrentVersion => "v1";

	// A fresh array each call, exactly as the interface requires - the caller may zero what it gets.
	public byte[] GetKey(string version) => version == CurrentVersion
		? [.. Key]
		: throw new Services.Keys.MasterSecretUnavailableException($"No master secret for version '{version}'.");
}
