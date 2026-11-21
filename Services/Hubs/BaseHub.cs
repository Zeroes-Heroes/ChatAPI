using Database.Context;
using Database.Entities;
using Database.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Services.Authorship.Interface;
using Services.Crypto;
using Services.Delivery.Interface;
using Services.Delivery.Service;
using Services.Extensions;
using Services.Hubs.Models;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Messaging.Interface;
using Services.Messaging.Models;
using Services.NotificationDispatch.Interface;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Statuses.Interface;
using Services.Statuses.Models;
using Services.Sync.Contracts;
using Services.Utilities;
using Services.Utilities.Statics;
using System.Security.Claims;

namespace Services.Hubs;

/*
Important things to note when working with SignalR Hubs:

Hubs are transient!
	* Don't store state in a property of the hub class.
	
	* Each hub method call is executed on a new hub instance.
	
	* Don't instantiate a hub directly via dependency injection.
		To send messages to a client from elsewhere in your application use
		IHubContext<T> : where T is Hub (ref. FriendshipService).

	* Use await when calling asynchronous methods that depend on the hub staying alive.
		For example, a method such as Clients.All.SendAsync(...) can fail if it's called without await
		and the hub method completes before SendAsync finishes.

Other things to note:
	- By default, SignalR uses the ClaimTypes.NameIdentifier from the ClaimsPrincipal associated 
	with the connection as the user identifier.

	- By default, hub method parameters are inspected and resolved from DI if possible.
	This means you can directly use dependency injection in the methods themselves.
	Or you can use DI the old-fashioned way, with a constructor.

	- You can change the name of the action/method using the [HubMethodName] attribute.

	- You can override the OnConnectAsync() and OnDisconnectAsync() methods.
*/

[Authorize]
public class BaseHub(IServiceScopeFactory serviceScopeFactory) : Hub
{
	public async override Task OnConnectedAsync()
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();
		ISessionTrustChecker trustChecker = serviceProvider.GetRequiredService<ISessionTrustChecker>();
		ISessionConnectionRegistry connectionRegistry = serviceProvider.GetRequiredService<ISessionConnectionRegistry>();

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		// The default authorization policy already requires an app-purpose, currently-trusted token
		// to reach this point (Services.Sessions.Authorization.AppSessionRequirement), so a SessionId
		// claim is always present here. Joining this group is what lets
		// Services.Sessions.Service.SessionRevocationNotifier reach exactly this device's own
		// connections - never the whole account/user - on revocation. Both this and the registration
		// below happen BEFORE the authoritative recheck further down, so a revoke committing in the gap
		// between SessionGuardHubFilter's own entry check and here still finds this connection
		// joinable/abortable rather than a blind spot until the next maintenance sweep.
		string? sessionId = user.FindFirstValue(SessionClaimTypes.SessionId);
		bool registered = false;
		if (sessionId is not null)
		{
			await Groups.AddToGroupAsync(Context.ConnectionId, Services.Sessions.Service.SessionRevocationNotifier.GroupName(sessionId));

			string? accountId = user.FindFirstValue(SessionClaimTypes.AccountContextId);
			string? deviceRegistrationId = user.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);

			// Stage 12: the address v1 live delivery uses. Joined here, from the session's OWN claims, so a
			// connection can only ever subscribe to its own device registration - and, because a registration
			// belongs to exactly one account context, a phone that started over cannot receive the superseded
			// context's messages over its new connection. Left automatically when the connection ends; the
			// Redis backplane is what makes the group reachable from the replica that consumed the task.
			if (deviceRegistrationId is not null)
			{
				await Groups.AddToGroupAsync(Context.ConnectionId, DeliveryGroups.ForDevice(deviceRegistrationId));
			}

			// Registers this exact connection so a later revocation can actually abort it even while
			// idle (ISessionConnectionRegistry), not merely notify it - see that interface's own doc
			// comment for the same-replica/cross-replica split.
			string? trustVersionClaim = user.FindFirstValue(SessionClaimTypes.TrustVersion);
			string? expClaim = user.FindFirstValue(JwtRegisteredClaimNames.Exp);
			if (accountId is not null && deviceRegistrationId is not null && long.TryParse(trustVersionClaim, out long trustVersion) && long.TryParse(expClaim, out long expSeconds))
			{
				// Captured as a local, not `this.Context`: a Hub instance is transient (see this file's
				// own top-of-file warning) - closing over `Context` (the HubCallerContext, owned by the
				// long-lived connection, not by any one transient Hub instance) rather than an implicit
				// `this` keeps this delegate from pinning this particular Hub instance alive.
				HubCallerContext callerContext = Context;
				// ExpiresAtUtc lets SessionMaintenanceHostedService abort an idle connection whose token has
				// since naturally expired without ever being revoked - a DB-independent, always-available
				// check (see that class's own doc comment).
				DateTimeOffset expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
				connectionRegistry.Register(new LiveSessionConnection(callerContext.ConnectionId, accountId, sessionId, deviceRegistrationId, trustVersion, expiresAtUtc, DateTimeOffset.UtcNow, () => callerContext.Abort()));
				registered = true;
			}
		}

		int receiverId = user.Id();

		// Stage 10 (plan/10-recipient-status.md): this sweep used to mark every undelivered message
		// Received for the reconnecting user and publish that to its authors. Reconnecting is not
		// evidence of anything about a message - "Received доказва, че конкретно устройство е записало
		// съобщението устойчиво, разполага с ключа и успешно е проверило и разшифровало съдържанието" -
		// and the device whose connection this is may never have held the message at all. The mark now
		// comes from that device's own receipt (the `message-receipt` method below), so nothing public is
		// written here any more and the author sees "sent" until a device really proves delivery.
		//
		// The authoritative trust recheck still runs: a connection admitted a moment before a concurrent
		// revoke's commit must not be left registered and addressable (see
		// ISessionTrustChecker.CheckAndLockDeviceForMutationAsync's own doc comment).
		TrustCheckResult trust = await trustChecker.CheckAsync(user, Context.ConnectionAborted);

		if (!trust.IsTrusted)
		{
			if (registered)
			{
				connectionRegistry.Unregister(Context.ConnectionId);
			}
			Context.Abort();
			return;
		}

		string cacheKey = string.Format(CacheKeys.ConnectionEstablished, receiverId);
		await cache.SetAsync(cacheKey, true, DateTime.UtcNow.AddHours(1));

		await base.OnConnectedAsync();
	}

	/// <summary>
	/// Per-user live delivery with the fresh-start boundary applied (plan/06): SignalR addresses users by
	/// legacy id, which cannot tell a phone's superseded context from its current one.
	/// </summary>
	private static async Task SendToLegacyUsersAsync<T>(
		IHubCallerClients clients, IAccountContextBoundary boundary, int[] legacyUserIds, int legacyChatId,
		string eventName, T payload, CancellationToken ct)
	{
		LegacyRecipients delivery = await boundary.ResolveLegacyRecipientsAsync(legacyUserIds, legacyChatId, ct);
		if (delivery.UserIds.Count > 0)
		{
			await clients.GetUsersByIds([.. delivery.UserIds]).SendAsync(eventName, payload, ct);
		}
		if (delivery.ConnectionIds.Count > 0)
		{
			await clients.Clients(delivery.ConnectionIds).SendAsync(eventName, payload, ct);
		}
	}

	public async override Task OnDisconnectedAsync(Exception? exception)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();

		// Safe cleanup regardless of whether this connection ever reached the registration point in
		// OnConnectedAsync (Unregister is a no-op for an unknown ConnectionId) - every disconnect path
		// (graceful stop, transport drop, or this.Abort() being called by the registry/filter itself)
		// ends here, so a connection can never be left registered after it is actually gone.
		serviceProvider.GetRequiredService<ISessionConnectionRegistry>().Unregister(Context.ConnectionId);

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int userId = user.Id();
		string connectionEstablishedKey = string.Format(CacheKeys.ConnectionEstablished, userId);
		string userCurrentChatIdKey = string.Format(CacheKeys.UserCurrentChatId, userId);

		await cache.RemoveAsync(connectionEstablishedKey);

		byte[]? currentChatIdBytes = await cache.GetAsync(userCurrentChatIdKey);
		if (currentChatIdBytes != null)
		{
			int currentChatId = System.Text.Json.JsonSerializer.Deserialize<int>(currentChatIdBytes);
			string chatEnteredKey = string.Format(CacheKeys.ChatEntered, userId, currentChatId);
			await cache.RemoveAsync(chatEnteredKey);
			await cache.RemoveAsync(userCurrentChatIdKey);
		}

		await base.OnDisconnectedAsync(exception);
	}

	[HubMethodName("send-message")]
	public async Task SendMessage(SendMessage sendMessageEvent)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();
		INotificationDispatch notificationDispatch = serviceProvider.GetRequiredService<INotificationDispatch>();
		ILogger<BaseHub> logger = serviceProvider.GetRequiredService<ILogger<BaseHub>>();
		ISessionTrustChecker trustChecker = serviceProvider.GetRequiredService<ISessionTrustChecker>();

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int senderId = user.Id();
		int chatId = sendMessageEvent.ChatId;
		MessageEntity? insertedMessage = null;

		// A new account context may never publish into the old context's chats (plan/06 fresh start).
		IAccountContextBoundary contextBoundary = serviceProvider.GetRequiredService<IAccountContextBoundary>();
		if (await contextBoundary.IsLegacyChatExcludedAsync(user, chatId, Context.ConnectionAborted))
		{
			return;
		}

		IChatAccessAuthority accessAuthority = serviceProvider.GetRequiredService<IChatAccessAuthority>();

		// SessionGuardHubFilter already re-checked trust on ENTRY to this invocation, but that alone
		// cannot stop a request admitted a moment before a concurrent revoke's commit from still
		// persisting/publishing after it - a plain second read immediately before this insert would
		// still race the exact same way. TrustedMutation locks the device(+session) row inside the SAME
		// transaction that performs the insert, which is what actually serializes this write against
		// DeviceTrustService.RevokeDeviceAsync/RefreshTokenService.RotateAsync for this caller (see
		// ISessionTrustChecker.CheckAndLockDeviceForMutationAsync's own doc comment for the full
		// lock-ordering argument).
		//
		// Stage 07 adds the second serialization point inside the very same transaction: the chat's own
		// counter row, which every membership mutation allocates its change sequence from. Checking the
		// publish right under that lock is what makes "Промените на правата се сериализират със записите в
		// чата" true - a concurrent removal/leave either commits first (and this send is then refused) or
		// waits until this message has committed.
		bool mayPublish = false;
		NewMessageStatusEffects statusEffects = NewMessageStatusEffects.None;
		IMessageStatusService statusService = serviceProvider.GetRequiredService<IMessageStatusService>();
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, user, async () =>
		{
			ChatAccess access = await accessAuthority.LockChatAndAuthorizeAsync(user, chatId, Context.ConnectionAborted);
			// Stage 09: CanSendNewMessages, not CanSend. A personal chat whose two people blocked each
			// other or stopped being contacts keeps everything it already has - readable history that goes
			// on receiving status updates - and only loses the right to create NEW correspondence. The
			// chat row taken just above is what serializes this against a concurrent block: either the
			// message commits before the block is visible, or this read waits and is then refused.
			mayPublish = access.CanSendNewMessages;
			if (!mayPublish)
			{
				// A frozen archive is read-only, a non-member has nothing here at all, and a suspended
				// personal chat accepts no new messages.
				return;
			}

			insertedMessage = dbContext.Messages.Add(new MessageEntity(sendMessageEvent.Content, senderId, chatId, DateTime.UtcNow)).Entity;
			await dbContext.SaveChangesAsync();

			// Stage 10, in the SAME transaction and under the SAME chat row: every eligible recipient's
			// unread goes up by exactly one, and this sender's own older incoming messages become
			// logically Seen with reason OwnSend. A crash between the message and these would otherwise
			// leave a committed message with no unread and a sender whose own send did not clear its
			// backlog.
			statusEffects = await statusService.RecordNewMessageAsync(
				user, access, chatId, insertedMessage, Context.ConnectionAborted);
			await dbContext.SaveChangesAsync();
		}, Context.ConnectionAborted);

		if (!trust.IsTrusted)
		{
			Context.Abort();
			return;
		}

		if (!mayPublish)
		{
			return;
		}

		MessageEntity messageEntity = insertedMessage!;

		int[] chatParticipants = (await dbContext.Chats.Where(c => c.Id == sendMessageEvent.ChatId).Select(c => c.Users.Select(u => u.Id)).FirstOrDefaultAsync() ?? []).Except([senderId]).ToArray();

		// Only currently active members receive new content; a removed/departed participant keeps its
		// archive but is not a recipient of anything created after it left (the legacy participant row it
		// still has is what preserves that archive, so it cannot be used as the recipient list).
		int[] receiversIds = [.. await accessAuthority.ActiveLegacyRecipientsAsync(chatId, chatParticipants, Context.ConnectionAborted)];

		// The server ACK, and nothing more: it proves the server stored the message
		// ("Server ACK доказва успешен запис на сървъра"). Stage 10 adds the denominator of this
		// message's group proportions - how many PEOPLE have the right to it, author excluded - because
		// no later event carries it and the author cannot derive it from a participant list.
		NewMessageCreatedEvent newMessageCreatedEvent = new(
			messageEntity.Id, messageEntity.ChatId, sendMessageEvent.TempId, messageEntity.CreatedAt, statusEffects.RecipientCount);
		NewMessageEvent newMessageEvent = new(messageEntity.Id, senderId, messageEntity.ChatId, sendMessageEvent.Content, messageEntity.CreatedAt);

		await Clients
			.GetUserById(senderId)
			.SendAsync(LiveEvents.NewMessageCreated, newMessageCreatedEvent);

		// Recipient-side context isolation: a phone that started over keeps the same legacy user id, so a
		// plain user-id fan-out would deliver this old-context chat to its fresh context's connection.
		await SendToLegacyUsersAsync(
			Clients, contextBoundary, receiversIds, chatId, LiveEvents.NewMessage, newMessageEvent, Context.ConnectionAborted);

		// Stage 10: presence used to be turned into a status here - "in the chat" became Seen and "online"
		// became Received, for a device that had not confirmed anything. Both are now the recipient
		// device's own business: it reports CiphertextStored/Received through `message-receipt` once its
		// local write really completed, and its Seen through `chat-entered`/a Seen receipt. What this
		// send DOES publish immediately is the sender's own OwnSend marking of its older incoming
		// messages, committed above.
		await PublishStatusMarksAsync(contextBoundary, chatId, statusEffects.OwnSendSeen);

		try
		{
			// Same fresh-start boundary as the live fan-out above, applied to the push: the payload is the
			// message text and the old chat id, and a device token is only known by legacy user id.
			IReadOnlyList<int> notifiableIds = await contextBoundary.NotifiableLegacyUserIdsAsync(
				receiversIds, chatId, Context.ConnectionAborted);
			if (notifiableIds.Count > 0)
			{
				await notificationDispatch.NotificationForNewMessage([.. notifiableIds], senderId, sendMessageEvent.Content, chatId);
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to dispatch push notifications for chat {ChatId}", chatId);
		}
	}

	/// <summary>
	/// Publishes committed status marks to the authors they belong to, strictly AFTER the write that
	/// produced them (stage 10). Nothing here decides anything: the marks are what
	/// <see cref="IMessageStatusService"/> actually committed, so a crash in between costs a live event
	/// and never a status - the next chat list / history read re-derives it from the committed rows.
	/// </summary>
	private async Task PublishStatusMarksAsync(
		IAccountContextBoundary contextBoundary, int legacyChatId, IReadOnlyList<RecipientStatusMark> marks)
	{
		foreach (RecipientStatusMark mark in marks)
		{
			MessageStatusUpdateEvent statusUpdate = new(
				legacyChatId, mark.RecipientLegacyUserId, mark.IsSeen ? MessageStatus.Seen : MessageStatus.Received,
				mark.AtUtc, [.. mark.LegacyMessageIds]);

			await SendToLegacyUsersAsync(
				Clients, contextBoundary, [mark.AuthorLegacyUserId], legacyChatId,
				LiveEvents.MessageStatusUpdate, statusUpdate, Context.ConnectionAborted);
		}
	}

	/// <summary>
	/// Stage 10 (plan/10-recipient-status.md): THE way a device proves what it holds - stored ciphertext,
	/// a readable/received copy, or a real observation. Every rule (idempotence, the first Seen fixing
	/// reason and time, unread, a frozen archive updating only private device progress, a terminal
	/// NotApplicable for a stale receipt) lives in <see cref="IMessageStatusService"/>; this method only
	/// translates the payload and publishes what was committed.
	///
	/// A new device proves its OWN delivery here: the row is keyed by this session's local instance, so
	/// the person's existing public marks never stand in for a copy this device does not have.
	///
	/// Answers the invocation with the per-message outcome, which is what makes a stale receipt actually
	/// terminate for the device that sent it - a refused batch answers null, the same "nothing to act on"
	/// every other mutating hub method gives. Nothing on the client queues receipts yet (that queue is
	/// stage 16); what this method owes the contract is a final answer, and it now returns one.
	/// </summary>
	// Named ReportMessageReceipts, not MessageReceipt: a method whose name equals the
	// Services.Statuses.Models.MessageReceipt type this body uses would shadow that type inside this
	// class. The wire name clients call is unchanged.
	[HubMethodName("message-receipt")]
	public async Task<MessageReceiptBatchResult?> ReportMessageReceipts(MessageReceiptBatch batch)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;

		ClaimsPrincipal? user = Context.User;

		if (user == null || batch?.Receipts is null)
			return null;

		// A new account context never reports about the old context's chats (plan/06 fresh start). The
		// access check below would refuse it anyway; this keeps the refusal identical to every other
		// surface's.
		IAccountContextBoundary contextBoundary = serviceProvider.GetRequiredService<IAccountContextBoundary>();
		if (await contextBoundary.IsLegacyChatExcludedAsync(user, batch.ChatId, Context.ConnectionAborted))
		{
			return null;
		}

		List<MessageReceipt> receipts = [];
		foreach (MessageReceiptItem item in batch.Receipts)
		{
			if (!Enum.IsDefined(typeof(ReceiptKind), item.Kind))
			{
				// An unknown kind is refused rather than guessed: a future kind must not silently become
				// one of today's three.
				continue;
			}

			ReceiptKind kind = (ReceiptKind)item.Kind;
			receipts.Add(new MessageReceipt(
				item.MessageId,
				kind,
				kind == ReceiptKind.Seen && item.ClientObservedAtUtc is DateTime observedAt
					? new ReportedObservation(observedAt, item.WasOffline)
					: null));
		}

		IMessageStatusService statusService = serviceProvider.GetRequiredService<IMessageStatusService>();
		Result<ReceiptBatchResult> result = await statusService.AcceptReceiptsAsync(
			user, batch.ChatId, receipts, Context.ConnectionAborted);

		if (!result.IsSuccess)
		{
			if (result.StatusCode == (int)System.Net.HttpStatusCode.Unauthorized)
			{
				// The caller stopped being trusted while this invocation ran - same treatment as every
				// other mutating hub method.
				Context.Abort();
			}

			return null;
		}

		await PublishStatusMarksAsync(contextBoundary, batch.ChatId, result.Data.Marks);

		// After the broadcast, like every other answer here: the outcomes describe committed state, and
		// the caller learns them only once that state is really published.
		return new MessageReceiptBatchResult
		{
			ChatId = result.Data.ChatId,
			Outcomes = [.. result.Data.Outcomes.Select(outcome => new MessageReceiptOutcomeItem
			{
				MessageId = outcome.LegacyMessageId,
				Disposition = (int)outcome.Disposition,
				StateVersion = outcome.StateVersion,
			})],
		};
	}

	/// <summary>
	/// Stage 11 (plan/11-message-acceptance.md), the three v1 surfaces this stage makes reachable. They
	/// exchange the pinned contract DTOs as JSON strings encoded with
	/// <see cref="SyncProtocol.CreateJsonOptions"/>, NOT as hub-serialized objects: the v1 encoding
	/// (camelCase, exact enum names, the leading <c>outcome</c> discriminator of
	/// <see cref="OperationResult"/>) has to hold regardless of which protocol this connection negotiated,
	/// and docs/sync-contract-v1.md forbids the legacy MessagePack resolver for v1 payloads. It also makes
	/// <see cref="SyncProtocol.MaxHubMessageBytes"/> enforceable on the real wire bytes rather than on a
	/// deserialized shape. A separately routed/versioned v1 hub is a stage-14 transport concern; the
	/// encoding, the limits and every authorization rule below are already the v1 ones.
	///
	/// Publishing the author context is deliberately its own method: the package is verified against the
	/// registry, so a key arriving WITH the package it signs could never be an authorization input.
	/// </summary>
	[HubMethodName("sync-register-author-key")]
	public async Task<string?> RegisterAuthorKey(string requestJson)
	{
		if (!TryReadSyncRequest(requestJson, out AuthorKeyRegistrationRequest? request)
			|| request!.Reference is null
			|| request.ProtocolVersion != SyncProtocol.Version
			|| !Base64Url.TryDecode(request.Spki, out byte[] spki))
		{
			return WriteSync(new SyncFailure(
				request?.ProtocolVersion is int version && version != SyncProtocol.Version
					? SyncErrorCode.UnsupportedVersion
					: SyncErrorCode.InvalidRequest,
				null));
		}

		ClaimsPrincipal? user = Context.User;
		if (user is null)
		{
			return null;
		}

		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		Result<Services.Authorship.Models.AuthorKeyRegistration> result = await scope.ServiceProvider
			.GetRequiredService<IAuthorKeyRegistry>()
			.RegisterAsync(user, request.Reference, spki, Context.ConnectionAborted);

		if (!result.IsSuccess)
		{
			if (result.StatusCode == (int)System.Net.HttpStatusCode.Unauthorized)
			{
				Context.Abort();
				return WriteSync(new SyncFailure(SyncErrorCode.SessionExpired, null));
			}

			return WriteSync(new SyncFailure(SyncErrorCode.InvalidRequest, null));
		}

		return WriteSync(new AuthorKeyRegistrationResult(result.Data.Reference, result.Data.AlreadyRegistered));
	}

	/// <summary>
	/// The v1 send. Everything it decides lives in <see cref="IMessageAcceptanceService"/>; this method
	/// only enforces the transport limit, hands the request over, and - strictly AFTER the commit - sends
	/// the server ACK back as this invocation's own return value and publishes the status marks the
	/// acceptance committed. Live delivery of the message itself is the outbox's (stage 12), which this
	/// transaction has already queued.
	/// </summary>
	[HubMethodName("sync-send-message")]
	public async Task<string?> SyncSendMessage(string requestJson)
	{
		if (System.Text.Encoding.UTF8.GetByteCount(requestJson ?? string.Empty) > SyncProtocol.MaxHubMessageBytes)
		{
			// "Enforce actual complete wire sizes at send/receive" - refused before anything is parsed.
			return WriteSync(new SyncFailure(SyncErrorCode.InvalidRequest, null));
		}

		if (!TryReadSyncRequest(requestJson, out SendMessageRequest? request))
		{
			return WriteSync(new SyncFailure(SyncErrorCode.InvalidRequest, null));
		}

		ClaimsPrincipal? user = Context.User;
		if (user is null)
		{
			return null;
		}

		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		Result<AcceptanceOutcome> result = await serviceProvider
			.GetRequiredService<IMessageAcceptanceService>()
			.AcceptAsync(user, request!, Context.ConnectionAborted);

		if (!result.IsSuccess)
		{
			return WriteSync(new SyncFailure(SyncErrorCode.InvalidRequest, null));
		}

		// The same OwnSend marks the legacy send publishes, for the same reason and in the same shape:
		// they describe committed state, so they are published only after the commit.
		await PublishStatusMarksAsync(
			serviceProvider.GetRequiredService<IAccountContextBoundary>(),
			LegacyChatIdOf(request!.Scope?.ChatId),
			result.Data.OwnSendSeen);

		if (result.Data.OutboxId is not null)
		{
			// Stage 12: this acceptance queued a row, so wake this replica's publisher instead of letting
			// live delivery wait for its next poll. Latency only - the row itself is what makes the message
			// deliverable, from any replica, after any crash.
			serviceProvider.GetRequiredService<IOutboxPublishSignal>().Notify();
		}

		string answer = WriteSync(result.Data.Result);

		if (result.Data.SessionUntrusted)
		{
			// Same treatment as every other mutating hub method; the answer above is Retryable, never
			// Rejected, so the client keeps its saved package.
			Context.Abort();
		}

		return answer;
	}

	/// <summary>
	/// Reconciles an operation whose answer was lost. Authenticates, checks the identity is this device's
	/// own, and answers the committed result - or <c>Unknown</c>, never <c>Rejected</c>, when nothing is
	/// committed yet. No membership check at all, by contract.
	/// </summary>
	[HubMethodName("sync-query-operation")]
	public async Task<string?> SyncQueryOperation(string requestJson)
	{
		if (!TryReadSyncRequest(requestJson, out OperationQuery? query))
		{
			return WriteSync(new SyncFailure(SyncErrorCode.InvalidRequest, null));
		}

		ClaimsPrincipal? user = Context.User;
		if (user is null)
		{
			return null;
		}

		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		Result<AcceptanceOutcome> result = await scope.ServiceProvider
			.GetRequiredService<IMessageAcceptanceService>()
			.QueryAsync(user, query!, Context.ConnectionAborted);

		return result.IsSuccess
			? WriteSync(result.Data.Result)
			: WriteSync(new SyncFailure(SyncErrorCode.InvalidRequest, null));
	}

	/// <summary>The v1 JSON options, built once: the contract's own encoding, never the legacy resolver's.</summary>
	private static readonly System.Text.Json.JsonSerializerOptions SyncJson = SyncProtocol.CreateJsonOptions();

	private static bool TryReadSyncRequest<T>(string? json, out T? request) where T : class
	{
		try
		{
			request = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<T>(json, SyncJson);
			return request is not null;
		}
		catch (System.Text.Json.JsonException)
		{
			request = null;
			return false;
		}
	}

	private static string WriteSync<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, SyncJson);

	/// <summary>The legacy chat id behind a v1 ChatId, or 0 when it names no legacy chat - which publishes nothing, because the mark list is then empty anyway.</summary>
	private static int LegacyChatIdOf(string? chatId) => int.TryParse(chatId, out int legacyChatId) ? legacyChatId : 0;

	[HubMethodName("chat-entered")]
	public async Task ChatEntered(ChatEnteredEvent chatEnteredEvent)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();

		int chatId = chatEnteredEvent.ChatId;
		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int receiverId = user.Id();

		// Same fresh-start boundary as every other surface: an excluded chat is not this context's
		// history, so entering it must not mark anything seen or reveal its messages.
		IAccountContextBoundary contextBoundary = serviceProvider.GetRequiredService<IAccountContextBoundary>();
		if (await contextBoundary.IsLegacyChatExcludedAsync(user, chatId, Context.ConnectionAborted))
		{
			return;
		}

		// Stage 07: a read-only archive publishes nothing - entering it must neither mark its messages Seen
		// for everyone else nor register this caller as present in it. The presence cache is this hub's own
		// concern (the push path reads it); the statuses are the status service's, which re-reads rights
		// under the chat lock inside its own transaction and refuses an archive there too.
		IChatAccessAuthority accessAuthority = serviceProvider.GetRequiredService<IChatAccessAuthority>();
		ChatAccess access = await accessAuthority.GetAsync(user, chatId, Context.ConnectionAborted);
		if (!access.CanSend)
		{
			return;
		}

		string cacheKey = string.Format(CacheKeys.ChatEntered, receiverId, chatId);
		string userCurrentChatIdKey = string.Format(CacheKeys.UserCurrentChatId, receiverId);
		await cache.SetAsync(cacheKey, chatId, DateTime.UtcNow.AddDays(1));
		await cache.SetAsync(userCurrentChatIdKey, chatId, DateTime.UtcNow.AddDays(1));

		// Stage 10: the chat being open is an OBSERVATION of this person, recorded in the logical model
		// with reason Observed - the first accepted transition fixes its time and takes the message out of
		// unread exactly once. This device's own observation is recorded separately, per local instance.
		// Per-message visibility (50 % for 500 ms, invisible mode, a covering screen) is stage 17.
		IMessageStatusService statusService = serviceProvider.GetRequiredService<IMessageStatusService>();
		Result<ReceiptBatchResult> result = await statusService.MarkChatObservedAsync(user, chatId, Context.ConnectionAborted);

		if (!result.IsSuccess)
		{
			if (result.StatusCode == (int)System.Net.HttpStatusCode.Unauthorized)
			{
				Context.Abort();
			}

			return;
		}

		await PublishStatusMarksAsync(contextBoundary, chatId, result.Data.Marks);
	}

	[HubMethodName("chat-exited")]
	public async Task ChatExited(ChatExitedEvent chatExitedEvent)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();
		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int userId = user.Id();
		string cacheKey = string.Format(CacheKeys.ChatEntered, userId, chatExitedEvent.ChatId);
		string userCurrentChatIdKey = string.Format(CacheKeys.UserCurrentChatId, userId);
		await cache.RemoveAsync(cacheKey);
		await cache.RemoveAsync(userCurrentChatIdKey);
	}

	[HubMethodName("message-status-update-received")]
	public async Task MessageStatusUpdateReceived(MessageStatusUpdateConfirmation messageStatusUpdateConfirmation)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int senderId = user.Id();
		DateTime now = DateTime.UtcNow;

		MessageStatusEntity[] messageStatusEntities =
			await dbContext.MessagesStatus.Where(ms => ms.Message.ChatId == messageStatusUpdateConfirmation.ChatId && !ms.StatusUpdateDeliveryConfirmed && ms.Status == messageStatusUpdateConfirmation.Status).ToArrayAsync();


		foreach (MessageStatusEntity messageStatusEntity in messageStatusEntities)
		{
			messageStatusEntity.StatusUpdateDeliveryConfirmed = true;
		}

		dbContext.UpdateRange(messageStatusEntities);
		await dbContext.SaveChangesAsync();
	}
}
