using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Authorship.Models;
using Services.Keys.Models;
using Services.Keys.Service;
using Services.Messaging.Models;
using Services.Sync.Contracts;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// plan/11-message-acceptance.md against a real PostgreSQL, real ES256 packages and the real services:
/// one transaction writes the message, the protected key, the operation result, the OwnSend changes,
/// the journal and the outbox; the server ACK describes exactly what committed; and a lost ACK, a
/// concurrent repeat and a membership change afterwards all still leave ONE MessageId and ONE logical
/// effect.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class MessageAcceptanceTests(DatabaseFixture fixture)
{
	/// <summary>A registered author context plus the first request built against it - the normal starting point.</summary>
	private sealed record Sender(
		TestAuthorKey Key, AuthorKeyReference Reference, MessageBinding Binding, SendMessageRequest Request, byte[] MessageKey)
		: IDisposable
	{
		public void Dispose() => Key.Dispose();
	}

	private static AuthorKeyReference ReferenceFor(TestMember member, int legacyChatId, string version = "1") =>
		new(member.Account.Id, legacyChatId.ToString(), $"authkey-{Guid.NewGuid():N}", version);

	private static MessageBinding BindingFor(TestMember member, AuthorKeyReference reference, string clientOperationId) =>
		new(SyncProtocol.Version,
			new OperationIdentity(
				new DeviceContext(member.Account.Id, member.Device.Id, member.Instance.Id), clientOperationId),
			reference.ChatId, reference, OperationKind.SendText);

	private static async Task<Sender> PrepareAsync(
		MembershipTestContext context, AppDbContext db, TestMember member, int legacyChatId,
		string clientOperationId = "op-1", string text = "hello")
	{
		TestAuthorKey key = new();
		AuthorKeyReference reference = ReferenceFor(member, legacyChatId);

		Result<AuthorKeyRegistration> registration = await context.AuthorKeys(db)
			.RegisterAsync(member.Principal, reference, key.Spki);
		Assert.True(registration.IsSuccess);
		Assert.False(registration.Data.AlreadyRegistered);

		MessageBinding binding = BindingFor(member, reference, clientOperationId);
		(string signedPackage, byte[] messageKey) = key.Build(binding, text);

		return new Sender(key, reference, binding, Request(legacyChatId, await ScopeAsync(db, member, legacyChatId), signedPackage, messageKey), messageKey);
	}

	private static SendMessageRequest Request(int legacyChatId, MembershipScope scope, string signedPackage, byte[] messageKey) =>
		new(SyncProtocol.Version, scope, signedPackage, Services.Crypto.Base64Url.Encode(messageKey));

	private static async Task<MembershipScope> ScopeAsync(AppDbContext db, TestMember member, int legacyChatId)
	{
		string chatId = legacyChatId.ToString();
		SyncMembershipEntity stint = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chatId && m.AccountId == member.Account.Id && m.State == SyncMembershipState.Active);
		return new MembershipScope(chatId, stint.Id, stint.RightsVersion.ToString());
	}

	private static AcceptedMessage AcceptedOf(Result<AcceptanceOutcome> result)
	{
		Assert.True(result.IsSuccess);
		AcceptedOperation accepted = Assert.IsType<AcceptedOperation>(result.Data.Result);
		return accepted.Result;
	}

	private static OperationErrorCode RejectedCodeOf(Result<AcceptanceOutcome> result)
	{
		Assert.True(result.IsSuccess);
		return Assert.IsType<RejectedOperation>(result.Data.Result).Code;
	}

	[Fact]
	public async Task Accepted_CommitsMessageKeyResultJournalAndOutbox_Together()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		Result<AcceptanceOutcome> result = await context.Acceptance(db).AcceptAsync(group.Founder.Principal, sender.Request);
		AcceptedMessage accepted = AcceptedOf(result);

		Assert.Equal(group.Chat.Id.ToString(), accepted.ChatId);
		Assert.Equal(accepted.MessageSequence, accepted.OwnSendThroughSequence);

		await using AppDbContext read = context.CreateContext();
		SyncMessageEntity message = await read.SyncMessages.AsNoTracking().SingleAsync(m => m.Id == accepted.MessageId);
		Assert.Equal(sender.Request.SignedPackage, message.SignedPackage);
		Assert.Equal(accepted.PackageDigest, message.PackageDigest);
		Assert.Equal(sender.Reference.AuthorKeyId, message.AuthorKeyId);

		// Each of the five other things the one transaction owes.
		SyncMessageKeyEntity key = await read.SyncMessageKeys.AsNoTracking().SingleAsync(k => k.MessageId == accepted.MessageId);
		Assert.Equal(group.Chat.Id.ToString(), key.ChatId);
		Assert.NotEmpty(key.ProtectedKey);

		SyncOperationResultEntity stored = await read.SyncOperationResults.AsNoTracking()
			.SingleAsync(r => r.ClientOperationId == "op-1" && r.AccountId == group.Founder.Account.Id);
		Assert.Equal(SyncOperationOutcome.Accepted, stored.Outcome);
		Assert.Equal(accepted.MessageId, stored.MessageId);
		Assert.Equal(accepted.PackageDigest, stored.PackageDigest);

		SyncChatChangeEntity journal = await read.SyncChatChanges.AsNoTracking()
			.SingleAsync(c => c.Kind == SyncChangeKind.Message && c.ReferenceId == accepted.MessageId);
		Assert.Equal(long.Parse(accepted.ChangeSequence), journal.ChangeSequence);

		SyncOutboxEntity outbox = await read.SyncOutbox.AsNoTracking().SingleAsync(o => o.MessageId == accepted.MessageId);
		Assert.Equal(SyncOutboxStatus.Pending, outbox.Status);
		Assert.Equal(outbox.Id, result.Data.OutboxId);
	}

	[Fact]
	public async Task ProtectedKey_UnwrapsOnlyUnderItsOwnMessageAndChat()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		AcceptedMessage accepted = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, sender.Request));

		await using AppDbContext read = context.CreateContext();
		SyncMessageKeyEntity row = await read.SyncMessageKeys.AsNoTracking().SingleAsync(k => k.MessageId == accepted.MessageId);
		MessageKeyProvisioningService provisioning = new(new AesGcmMessageKeyProtector(new FixedMasterSecretProvider()));

		// The exact key the client sent comes back out - the server stored the real content key, wrapped.
		Assert.Equal(
			sender.MessageKey,
			provisioning.UnwrapForAuthorizedDelivery(row, new MessageKeyAccessContext(accepted.MessageId, accepted.ChatId)));

		// And the envelope is bound to its own identifiers: a mismatched context fails authentication.
		Assert.Throws<Services.Keys.KeyContextMismatchException>(() =>
			provisioning.UnwrapForAuthorizedDelivery(row, new MessageKeyAccessContext(accepted.MessageId, "999999")));
	}

	[Fact]
	public async Task LostAck_RetryOfTheSamePackage_ReturnsTheOriginalResult_AndWritesNothingNew()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		AcceptedMessage first = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, sender.Request));

		// The client never saw the ACK and resends the SAME saved package.
		Result<AcceptanceOutcome> retry = await context.Acceptance(db).AcceptAsync(group.Founder.Principal, sender.Request);
		AcceptedMessage second = AcceptedOf(retry);

		Assert.Equal(first.MessageId, second.MessageId);
		Assert.Equal(first.CreatedAtUtc, second.CreatedAtUtc);
		Assert.Equal(first.MessageSequence, second.MessageSequence);
		Assert.Equal(first.ChangeSequence, second.ChangeSequence);
		Assert.Equal(first.OwnSendThroughSequence, second.OwnSendThroughSequence);
		// A retry queues no second publish.
		Assert.Null(retry.Data.OutboxId);

		await using AppDbContext read = context.CreateContext();
		string chatId = group.Chat.Id.ToString();
		Assert.Equal(1, await read.SyncMessages.CountAsync(m => m.ChatId == chatId));
		Assert.Equal(1, await read.SyncOutbox.CountAsync(o => o.ChatId == chatId));
		Assert.Equal(1, await read.SyncChatChanges.CountAsync(c => c.ChatId == chatId && c.Kind == SyncChangeKind.Message));
	}

	[Fact]
	public async Task DifferentPackage_SameOperationIdentity_IsRejectedAndLeavesTheOriginalResultIntact()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		AcceptedMessage first = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, sender.Request));

		// Same identity, different bytes (a new random key/nonce makes a genuinely different package).
		(string otherPackage, byte[] otherKey) = sender.Key.Build(sender.Binding, "hello");
		SendMessageRequest conflicting = Request(group.Chat.Id, sender.Request.Scope, otherPackage, otherKey);

		Assert.Equal(OperationErrorCode.OperationConflict,
			RejectedCodeOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, conflicting)));

		await using AppDbContext read = context.CreateContext();
		SyncOperationResultEntity stored = await read.SyncOperationResults.AsNoTracking()
			.SingleAsync(r => r.ClientOperationId == "op-1" && r.AccountId == group.Founder.Account.Id);
		Assert.Equal(SyncOperationOutcome.Accepted, stored.Outcome);
		Assert.Equal(first.MessageId, stored.MessageId);
		Assert.Equal(1, await read.SyncMessages.CountAsync(m => m.ChatId == group.Chat.Id.ToString()));
	}

	[Fact]
	public async Task AcceptedOperation_IsStillRecognized_AfterTheSenderIsRemovedFromTheChat()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Second, group.Chat.Id);

		AcceptedMessage first = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Second.Principal, sender.Request));

		Result<Services.Membership.Models.ChatMembershipChange> removal = await context.Membership(db)
			.RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId);
		Assert.True(removal.IsSuccess);

		// The retry answers the ORIGINAL result, with no rights check in front of it...
		AcceptedMessage retried = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Second.Principal, sender.Request));
		Assert.Equal(first.MessageId, retried.MessageId);
		Assert.Equal(first.OwnSendThroughSequence, retried.OwnSendThroughSequence);

		// ...and so does an explicit reconciliation.
		AcceptedMessage queried = AcceptedOf(await context.Acceptance(db)
			.QueryAsync(group.Second.Principal, new OperationQuery(SyncProtocol.Version, sender.Binding.Operation)));
		Assert.Equal(first.MessageId, queried.MessageId);
	}

	[Fact]
	public async Task QueryOperation_IsUnknownBeforeTheCommit_AndRefusesAnotherDevicesOperation()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		Result<AcceptanceOutcome> before = await context.Acceptance(db)
			.QueryAsync(group.Founder.Principal, new OperationQuery(SyncProtocol.Version, sender.Binding.Operation));
		Assert.True(before.IsSuccess);
		// "Timeout/disconnect means Unknown, never Rejected."
		Assert.IsType<UnknownOperation>(before.Data.Result);

		// Someone else's identity is not probeable, and is not answered "Unknown" either.
		Result<AcceptanceOutcome> foreign = await context.Acceptance(db)
			.QueryAsync(group.Second.Principal, new OperationQuery(SyncProtocol.Version, sender.Binding.Operation));
		Assert.Equal(OperationErrorCode.Forbidden, RejectedCodeOf(foreign));
	}

	[Fact]
	public async Task ConcurrentRepeats_OfOneOperation_ProduceOneMessage()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		// Two independent connections (their own DbContexts, like two replicas) racing the same package.
		await using AppDbContext one = context.CreateContext();
		await using AppDbContext two = context.CreateContext();
		Task<Result<AcceptanceOutcome>> first = context.Acceptance(one).AcceptAsync(group.Founder.Principal, sender.Request);
		Task<Result<AcceptanceOutcome>> second = context.Acceptance(two).AcceptAsync(group.Founder.Principal, sender.Request);
		Result<AcceptanceOutcome>[] results = await Task.WhenAll(first, second);

		AcceptedMessage a = AcceptedOf(results[0]);
		AcceptedMessage b = AcceptedOf(results[1]);
		Assert.Equal(a.MessageId, b.MessageId);

		await using AppDbContext read = context.CreateContext();
		Assert.Equal(1, await read.SyncMessages.CountAsync(m => m.ChatId == group.Chat.Id.ToString()));
		Assert.Equal(1, await read.SyncOutbox.CountAsync(o => o.ChatId == group.Chat.Id.ToString()));
	}

	[Fact]
	public async Task NewMessage_RaisesEveryRecipientsUnread_AndJournalsIt()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		AcceptedMessage accepted = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, sender.Request));

		await using AppDbContext read = context.CreateContext();
		string chatId = group.Chat.Id.ToString();
		var unread = await read.SyncUnreadStates.AsNoTracking()
			.Where(u => u.ChatId == chatId)
			.Select(u => new { u.Count, u.OldestUnreadMessageId, u.LatestUnreadMessageId, u.Membership.AccountId })
			.ToListAsync();

		// The two OTHER people, and not the author.
		Assert.Equal(2, unread.Count);
		Assert.All(unread, u =>
		{
			Assert.Equal(1, u.Count);
			Assert.Equal(accepted.MessageId, u.OldestUnreadMessageId);
			Assert.Equal(accepted.MessageId, u.LatestUnreadMessageId);
		});
		Assert.DoesNotContain(unread, u => u.AccountId == group.Founder.Account.Id);

		Assert.Equal(2, await read.SyncChatChanges.CountAsync(c => c.ChatId == chatId && c.Kind == SyncChangeKind.Unread));
	}

	[Fact]
	public async Task OwnSend_MarksOlderIncomingSeen_KeepsItsBoundary_AndDoesNotMoveOnRetry()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		// The other person writes first.
		using Sender incoming = await PrepareAsync(context, db, group.Second, group.Chat.Id, "op-incoming", "theirs");
		AcceptedMessage older = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Second.Principal, incoming.Request));

		// Then this sender replies - which is what marks the older incoming message read for it.
		using Sender reply = await PrepareAsync(context, db, group.Founder, group.Chat.Id, "op-reply", "mine");
		AcceptedMessage mine = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, reply.Request));

		Assert.Equal(mine.MessageSequence, mine.OwnSendThroughSequence);

		await using AppDbContext read = context.CreateContext();
		SyncMessageRecipientStateEntity state = await read.SyncMessageRecipientStates.AsNoTracking()
			.SingleAsync(s => s.MessageId == older.MessageId && s.AccountId == group.Founder.Account.Id);
		Assert.Equal(SyncSeenReason.OwnSend, state.SeenReason);
		Assert.NotNull(state.SeenAtUtc);
		// Seen implies the person holds it: the author's view must not be able to go backwards.
		Assert.NotNull(state.ReceivedAtUtc);

		SyncUnreadStateEntity senderUnread = await read.SyncUnreadStates.AsNoTracking()
			.SingleAsync(u => u.ChatId == group.Chat.Id.ToString() && u.Membership.AccountId == group.Founder.Account.Id);
		Assert.Equal(0, senderUnread.Count);

		// A LATER incoming message stays unread, and the retry answers the original boundary.
		using Sender later = await PrepareAsync(context, db, group.Second, group.Chat.Id, "op-later", "after");
		AcceptedMessage afterwards = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Second.Principal, later.Request));

		AcceptedMessage retried = AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, reply.Request));
		Assert.Equal(mine.OwnSendThroughSequence, retried.OwnSendThroughSequence);

		await using AppDbContext after = context.CreateContext();
		Assert.False(await after.SyncMessageRecipientStates.AsNoTracking()
			.AnyAsync(s => s.MessageId == afterwards.MessageId && s.AccountId == group.Founder.Account.Id && s.SeenAtUtc != null));
		Assert.Equal(1, (await after.SyncUnreadStates.AsNoTracking()
			.SingleAsync(u => u.ChatId == group.Chat.Id.ToString() && u.Membership.AccountId == group.Founder.Account.Id)).Count);
	}

	[Fact]
	public async Task UnregisteredAuthorKey_IsRejected_AndNothingIsWrittenOrRemembered()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		using TestAuthorKey key = new();
		AuthorKeyReference reference = ReferenceFor(group.Founder, group.Chat.Id);
		MessageBinding binding = BindingFor(group.Founder, reference, "op-1");
		(string signedPackage, byte[] messageKey) = key.Build(binding, "hello");
		SendMessageRequest request = Request(group.Chat.Id, await ScopeAsync(db, group.Founder, group.Chat.Id), signedPackage, messageKey);

		Assert.Equal(OperationErrorCode.InvalidPackage,
			RejectedCodeOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, request)));

		await using AppDbContext read = context.CreateContext();
		Assert.Empty(await read.SyncMessages.AsNoTracking().Where(m => m.ChatId == group.Chat.Id.ToString()).ToListAsync());
		// Not remembered as terminal: registering the author context and retrying the SAME package works.
		Assert.Empty(await read.SyncOperationResults.AsNoTracking().Where(r => r.ClientOperationId == "op-1").ToListAsync());

		Assert.True((await context.AuthorKeys(db).RegisterAsync(group.Founder.Principal, reference, key.Spki)).IsSuccess);
		Assert.Equal("op-1",
			AcceptedOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, request)).Operation.ClientOperationId);
	}

	[Fact]
	public async Task ForeignSignature_IsRejectedTerminally_AndTheSameBytesAnswerIdentically()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		// The reference is registered with ONE key; the package is signed with another.
		using TestAuthorKey registered = new();
		using TestAuthorKey impostor = new();
		AuthorKeyReference reference = ReferenceFor(group.Founder, group.Chat.Id);
		Assert.True((await context.AuthorKeys(db).RegisterAsync(group.Founder.Principal, reference, registered.Spki)).IsSuccess);

		MessageBinding binding = BindingFor(group.Founder, reference, "op-foreign");
		(string signedPackage, byte[] messageKey) = impostor.Build(binding, "hello");
		SendMessageRequest request = Request(group.Chat.Id, await ScopeAsync(db, group.Founder, group.Chat.Id), signedPackage, messageKey);

		Assert.Equal(OperationErrorCode.InvalidPackage,
			RejectedCodeOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, request)));
		Assert.Equal(OperationErrorCode.InvalidPackage,
			RejectedCodeOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, request)));

		await using AppDbContext read = context.CreateContext();
		SyncOperationResultEntity stored = await read.SyncOperationResults.AsNoTracking()
			.SingleAsync(r => r.ClientOperationId == "op-foreign");
		Assert.Equal(SyncOperationOutcome.Rejected, stored.Outcome);
		Assert.Null(stored.MessageId);
		Assert.Empty(await read.SyncMessages.AsNoTracking().Where(m => m.ChatId == group.Chat.Id.ToString()).ToListAsync());
	}

	[Fact]
	public async Task PackageBoundToAnotherDevice_IsRefused()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		using TestAuthorKey key = new();
		AuthorKeyReference reference = ReferenceFor(group.Founder, group.Chat.Id);
		Assert.True((await context.AuthorKeys(db).RegisterAsync(group.Founder.Principal, reference, key.Spki)).IsSuccess);

		// A well-formed, correctly signed package that names a DIFFERENT local instance than the session.
		MessageBinding foreignDevice = new(
			SyncProtocol.Version,
			new OperationIdentity(
				new DeviceContext(group.Founder.Account.Id, group.Founder.Device.Id, "inst-somebody-else"), "op-1"),
			reference.ChatId, reference, OperationKind.SendText);
		(string signedPackage, byte[] messageKey) = key.Build(foreignDevice, "hello");

		Assert.Equal(OperationErrorCode.Forbidden, RejectedCodeOf(await context.Acceptance(db).AcceptAsync(
			group.Founder.Principal,
			Request(group.Chat.Id, await ScopeAsync(db, group.Founder, group.Chat.Id), signedPackage, messageKey))));
	}

	[Fact]
	public async Task StaleScope_AndUnsupportedVersion_AreRefusedWithoutWriting()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		// The scope is checked against the authoritative stint, never believed.
		SendMessageRequest staleRights = sender.Request with
		{
			Scope = sender.Request.Scope with { RightsVersion = "999" },
		};
		Assert.Equal(OperationErrorCode.MembershipChanged,
			RejectedCodeOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, staleRights)));

		SendMessageRequest wrongStint = sender.Request with
		{
			Scope = sender.Request.Scope with { MembershipId = Guid.NewGuid().ToString("D") },
		};
		Assert.Equal(OperationErrorCode.MembershipChanged,
			RejectedCodeOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, wrongStint)));

		Assert.Equal(OperationErrorCode.UnsupportedVersion,
			RejectedCodeOf(await context.Acceptance(db).AcceptAsync(group.Founder.Principal, sender.Request with { ProtocolVersion = 2 })));

		await using AppDbContext read = context.CreateContext();
		Assert.Empty(await read.SyncMessages.AsNoTracking().Where(m => m.ChatId == group.Chat.Id.ToString()).ToListAsync());
	}

	[Fact]
	public async Task FrozenArchive_AcceptsNoNewMessage()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Second, group.Chat.Id);

		Assert.True((await context.Membership(db)
			.RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId)).IsSuccess);

		Assert.Equal(OperationErrorCode.MembershipChanged,
			RejectedCodeOf(await context.Acceptance(db).AcceptAsync(group.Second.Principal, sender.Request)));

		await using AppDbContext read = context.CreateContext();
		Assert.Empty(await read.SyncMessages.AsNoTracking().Where(m => m.ChatId == group.Chat.Id.ToString()).ToListAsync());
	}

	[Fact]
	public async Task RevokedDevice_IsRefusedTransiently_AndWritesNothing()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		using Sender sender = await PrepareAsync(context, db, group.Founder, group.Chat.Id);

		SyncDeviceRegistrationEntity device = await db.SyncDeviceRegistrations.SingleAsync(d => d.Id == group.Founder.Device.Id);
		device.TrustState = DeviceTrustState.Revoked;
		device.TrustVersion++;
		await db.SaveChangesAsync();

		Result<AcceptanceOutcome> result = await context.Acceptance(db).AcceptAsync(group.Founder.Principal, sender.Request);
		Assert.True(result.IsSuccess);
		RetryableOperation retryable = Assert.IsType<RetryableOperation>(result.Data.Result);
		// Transient, never Rejected: the saved package must survive a revocation the device can recover from.
		Assert.Equal(OperationErrorCode.SessionExpired, retryable.Code);
		Assert.True(retryable.RetryAfterMs >= 0);
		Assert.True(result.Data.SessionUntrusted);

		await using AppDbContext read = context.CreateContext();
		Assert.Empty(await read.SyncMessages.AsNoTracking().Where(m => m.ChatId == group.Chat.Id.ToString()).ToListAsync());
		Assert.Empty(await read.SyncOperationResults.AsNoTracking().Where(r => r.ClientOperationId == "op-1").ToListAsync());
	}

	[Fact]
	public async Task AuthorKeyRegistry_BindsOnce_IsIdempotent_AndRefusesForeignAccountsAndRebindings()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		using TestAuthorKey key = new();
		using TestAuthorKey other = new();
		AuthorKeyReference reference = ReferenceFor(group.Founder, group.Chat.Id);

		Assert.False((await context.AuthorKeys(db).RegisterAsync(group.Founder.Principal, reference, key.Spki)).Data.AlreadyRegistered);
		// A repeat of the identical binding is a confirmation, not a second write (a recovered or
		// re-approved device restoring the ORIGINAL key lands here).
		Assert.True((await context.AuthorKeys(db).RegisterAsync(group.Founder.Principal, reference, key.Spki)).Data.AlreadyRegistered);

		// A different key for a bound reference is a conflict - never an overwrite.
		Result<AuthorKeyRegistration> conflict = await context.AuthorKeys(db).RegisterAsync(group.Founder.Principal, reference, other.Spki);
		Assert.False(conflict.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.Conflict, conflict.StatusCode);

		// Somebody else's account context cannot be published for, even in a chat both are in.
		Result<AuthorKeyRegistration> foreign = await context.AuthorKeys(db).RegisterAsync(group.Second.Principal, reference, other.Spki);
		Assert.False(foreign.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.Forbidden, foreign.StatusCode);

		// And a chat this caller is not in answers the same 404 as a chat that does not exist.
		Result<AuthorKeyRegistration> outsider = await context.AuthorKeys(db).RegisterAsync(
			group.Outsider.Principal, ReferenceFor(group.Outsider, group.Chat.Id), other.Spki);
		Assert.False(outsider.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.NotFound, outsider.StatusCode);

		await using AppDbContext read = context.CreateContext();
		SyncAuthorKeyEntity stored = await read.SyncAuthorKeys.AsNoTracking()
			.SingleAsync(k => k.AccountId == reference.AccountContextId && k.AuthorKeyId == reference.AuthorKeyId);
		Assert.Equal(key.Spki, stored.Spki);
		Assert.Equal(group.Founder.Device.Id, stored.RegisteredByDeviceRegistrationId);
	}
}
