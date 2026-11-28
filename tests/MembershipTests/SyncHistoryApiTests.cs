using System.Security.Cryptography;
using System.Text;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Authorship.Models;
using Services.Crypto;
using Services.Membership.Models;
using Services.Messaging.Models;
using Services.Sync.Contracts;
using Services.Sync.Interface;
using Services.Sync.Models;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// plan/13-history-api.md against a real PostgreSQL and the real services: the handshake fixes the
/// catalog boundary, the priority set and the whole boundary set durably; the HTTP pages answer
/// permitted chats, messages, keys, statuses and metadata inside those fixed bounds; cursors are
/// idempotent and non-skipping while messages keep arriving; a frozen archive answers from its own
/// snapshot; a rights change, a foreign cursor and a pruned journal are refused with the contract's own
/// codes; and the position exchanges are what make a session ready and complete an initial restoration.
///
/// Nothing here is a stand-in: the services under test are composed exactly as
/// <see cref="Services.Extensions.ServiceCollectionExtension"/> composes them, over the real access
/// authority, the real author-key registry, the real key module and the real trust checker.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SyncHistoryApiTests(DatabaseFixture fixture)
{
	/// <summary>One accepted v1 message plus what a test needs to check what was answered about it.</summary>
	private sealed record Accepted(AcceptedMessage Message, MessageBinding Binding, byte[] Spki, string Plaintext);

	private static SessionContext SessionOf(TestMember member) =>
		new(new DeviceContext(member.Account.Id, member.Device.Id, member.Instance.Id),
			member.Session.Id,
			member.Device.TrustVersion.ToString());

	/// <summary>Accepts a real signed package exactly as the hub's v1 send does.</summary>
	private static async Task<Accepted> SendAsync(
		MembershipTestContext context, AppDbContext db, TestMember sender, int legacyChatId, string text)
	{
		using TestAuthorKey key = new();
		AuthorKeyReference reference = new(sender.Account.Id, legacyChatId.ToString(), $"authkey-{Guid.NewGuid():N}", "1");

		Result<AuthorKeyRegistration> registered = await context.AuthorKeys(db).RegisterAsync(sender.Principal, reference, key.Spki);
		Assert.True(registered.IsSuccess);

		string chatId = legacyChatId.ToString();
		SyncMembershipEntity stint = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chatId && m.AccountId == sender.Account.Id && m.State == SyncMembershipState.Active);

		MessageBinding binding = new(
			SyncProtocol.Version,
			new OperationIdentity(new DeviceContext(sender.Account.Id, sender.Device.Id, sender.Instance.Id), $"op-{Guid.NewGuid():N}"),
			chatId, reference, OperationKind.SendText);
		(string signedPackage, byte[] messageKey) = key.Build(binding, text);

		Result<AcceptanceOutcome> result = await context.Acceptance(db).AcceptAsync(
			sender.Principal,
			new SendMessageRequest(
				SyncProtocol.Version,
				new MembershipScope(chatId, stint.Id, stint.RightsVersion.ToString()),
				signedPackage,
				Base64Url.Encode(messageKey)));

		Assert.True(result.IsSuccess);
		AcceptedOperation accepted = Assert.IsType<AcceptedOperation>(result.Data.Result);
		return new Accepted(accepted.Result, binding, key.Spki, text);
	}

	private static async Task<SyncHandshakeResponse> HandshakeAsync(
		MembershipTestContext context, AppDbContext db, TestMember member, string connectionId)
	{
		SyncResult<SyncHandshakeResponse> result = await context.SyncSessions(db).HandshakeAsync(
			member.Principal, connectionId, new SyncHandshakeRequest(SyncProtocol.Version, SessionOf(member)));

		Assert.Null(result.Failure);
		return result.Value!;
	}

	/// <summary>Every fixed boundary of a session, walked through the real boundary pages.</summary>
	private static async Task<List<SyncBoundary>> AllBoundariesAsync(
		MembershipTestContext context, AppDbContext db, TestMember member, SyncHandshakeResponse handshake, int limit = 25)
	{
		List<SyncBoundary> all = [];
		string? cursor = handshake.BoundaryCursor;
		while (cursor is not null)
		{
			SyncResult<SyncBoundaryPage> page = await context.SyncSessions(db).GetBoundaryPageAsync(
				member.Principal, new SyncBoundaryPageRequest(SyncProtocol.Version, handshake.SyncSessionId, cursor, limit));

			Assert.Null(page.Failure);
			all.AddRange(page.Value!.Boundaries);
			Assert.Equal(page.Value.Complete, page.Value.NextCursor is null);
			cursor = page.Value.NextCursor;
		}

		return all;
	}

	// ---------------------------------------------------------------- handshake

	[Fact]
	public async Task Handshake_FixesTheCatalogBoundaryAndTheWholeBoundarySet_InFullMode()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Founder, "conn-1");

		Assert.Equal(SyncProtocol.Version, handshake.ProtocolVersion);
		Assert.Equal(SyncMode.Full, handshake.Mode);
		Assert.Equal("conn-1", handshake.ConnectionId);
		Assert.Null(handshake.CatalogBoundary.Scope);
		Assert.Equal(StreamKind.Catalog, handshake.CatalogBoundary.Through.StreamKind);
		Assert.True(SyncProtocol.IsSequence(handshake.CatalogBoundary.Through.Sequence));
		Assert.NotNull(handshake.BoundaryCursor);
		Assert.Contains(group.Chat.Id.ToString(), handshake.PriorityChatIds);
		Assert.True(handshake.PriorityChatIds.Count <= SyncProtocol.MaxPriorityChats);

		// The fixed set is DURABLE, not a value computed per request: another replica reading the same
		// rows answers the same boundaries.
		await using AppDbContext replica = context.CreateContext();
		SyncSessionEntity session = await replica.SyncSessions.AsNoTracking().SingleAsync(s => s.Id == handshake.SyncSessionId);
		Assert.Equal(group.Founder.Account.Id, session.AccountId);
		Assert.Equal(group.Founder.Instance.Id, session.LocalInstanceId);
		Assert.Equal(session.BoundaryCount, await replica.SyncSessionBoundaries.CountAsync(b => b.SyncSessionId == session.Id));

		List<SyncBoundary> boundaries = await AllBoundariesAsync(context, db, group.Founder, handshake);
		SyncBoundary chat = Assert.Single(boundaries, b => b.Scope!.ChatId == group.Chat.Id.ToString());
		Assert.Equal(StreamKind.Chat, chat.Through.StreamKind);
		Assert.Equal(group.Chat.Id.ToString(), chat.Through.ChatId);
		Assert.Equal(chat.Scope!.MembershipId, chat.Through.MembershipId);
		Assert.Equal(handshake.CatalogBoundary.SnapshotId, chat.SnapshotId);
	}

	[Fact]
	public async Task Handshake_RepeatedOnTheSameConnection_ContinuesTheSessionItAlreadyFixed()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		// Unique per run: this database is durable and shared by the whole test collection, so a count of
		// rows carrying this connection id must be about THIS test.
		string connection = $"conn-retry-{Guid.NewGuid():N}";
		SyncHandshakeResponse first = await HandshakeAsync(context, db, group.Founder, connection);
		SyncHandshakeResponse again = await HandshakeAsync(context, db, group.Founder, connection);

		// A lost answer costs a round trip, never a second boundary set for one connection.
		Assert.Equal(first.SyncSessionId, again.SyncSessionId);
		Assert.Equal(first.CatalogBoundary, again.CatalogBoundary);
		Assert.Equal(first.RightsVersion, again.RightsVersion);

		await using AppDbContext read = context.CreateContext();
		Assert.Equal(1, await read.SyncSessions.CountAsync(s => s.ConnectionId == connection));

		// A different connection is a different sync session: reconnecting repeats the handshake.
		SyncHandshakeResponse reconnected = await HandshakeAsync(context, db, group.Founder, $"{connection}-other");
		Assert.NotEqual(first.SyncSessionId, reconnected.SyncSessionId);
	}

	[Fact]
	public async Task Handshake_RefusesASessionContextThatIsNotTheCallersOwn()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		string connection = $"conn-refused-{Guid.NewGuid():N}";

		SyncResult<SyncHandshakeResponse> foreign = await context.SyncSessions(db).HandshakeAsync(
			group.Founder.Principal, connection, new SyncHandshakeRequest(SyncProtocol.Version, SessionOf(group.Second)));
		Assert.Equal(SyncErrorCode.InvalidRequest, foreign.Failure!.Code);

		SyncResult<SyncHandshakeResponse> wrongVersion = await context.SyncSessions(db).HandshakeAsync(
			group.Founder.Principal, connection, new SyncHandshakeRequest(SyncProtocol.Version + 1, SessionOf(group.Founder)));
		Assert.Equal(SyncErrorCode.UnsupportedVersion, wrongVersion.Failure!.Code);

		// Nothing was fixed for that connection - a refused handshake writes no session at all.
		await using AppDbContext read = context.CreateContext();
		Assert.False(await read.SyncSessions.AnyAsync(s => s.ConnectionId == connection));
	}

	[Fact]
	public async Task PrioritySet_LeadsWithTheNewestIncomingUnread_AndIsFilledWithTheLatestActiveChats()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		TestMember owner = await context.CreateMemberAsync(db);
		TestMember partner = await context.CreateMemberAsync(db);

		// Three chats of the same person: the middle one receives the newest unread message.
		List<ChatEntity> chats = [];
		for (int i = 0; i < 3; i++)
		{
			ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, $"Chat {i}", owner.LegacyUserId, partner.LegacyUserId);
			await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
			{
				await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [owner.LegacyUserId, partner.LegacyUserId]);
				await tx.CommitAsync();
			}

			chats.Add(chat);
		}

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		await SendAsync(context, db, partner, chats[0].Id, "older incoming");
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		await SendAsync(context, db, partner, chats[1].Id, "newest incoming");

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, owner, "conn-priority");

		// "Приоритетът се определя от най-скорошното входящо логически непрочетено съобщение", newest first.
		Assert.Equal(chats[1].Id.ToString(), handshake.PriorityChatIds[0]);
		Assert.Equal(chats[0].Id.ToString(), handshake.PriorityChatIds[1]);

		// "Когато тези чатове са под 10, наборът се допълва с последните активни без повторения."
		Assert.Contains(chats[2].Id.ToString(), handshake.PriorityChatIds);
		Assert.Equal(handshake.PriorityChatIds.Distinct().Count(), handshake.PriorityChatIds.Count);
	}

	// ---------------------------------------------------------------- boundary pages

	[Fact]
	public async Task BoundaryPage_IsBounded_AndARetriedCursorAnswersTheSamePageAndContinuation()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		TestMember owner = await context.CreateMemberAsync(db);
		TestMember partner = await context.CreateMemberAsync(db);

		for (int i = 0; i < 3; i++)
		{
			ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, $"B{i}", owner.LegacyUserId, partner.LegacyUserId);
			await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync();
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [owner.LegacyUserId, partner.LegacyUserId]);
			await tx.CommitAsync();
		}

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, owner, "conn-boundary");

		SyncResult<SyncBoundaryPage> first = await context.SyncSessions(db).GetBoundaryPageAsync(
			owner.Principal, new SyncBoundaryPageRequest(SyncProtocol.Version, handshake.SyncSessionId, handshake.BoundaryCursor!, 2));
		Assert.Null(first.Failure);
		Assert.Equal(2, first.Value!.Boundaries.Count);
		Assert.False(first.Value.Complete);
		Assert.NotNull(first.Value.NextCursor);

		// The retry: same items, same continuation, nothing advanced.
		SyncResult<SyncBoundaryPage> retried = await context.SyncSessions(db).GetBoundaryPageAsync(
			owner.Principal, new SyncBoundaryPageRequest(SyncProtocol.Version, handshake.SyncSessionId, handshake.BoundaryCursor!, 2));
		Assert.Null(retried.Failure);
		Assert.Equal(first.Value.Boundaries, retried.Value!.Boundaries);
		Assert.Equal(first.Value.NextCursor, retried.Value.NextCursor);

		SyncResult<SyncBoundaryPage> second = await context.SyncSessions(db).GetBoundaryPageAsync(
			owner.Principal, new SyncBoundaryPageRequest(SyncProtocol.Version, handshake.SyncSessionId, first.Value.NextCursor!, 2));
		Assert.Null(second.Failure);
		Assert.True(second.Value!.Complete);
		Assert.Null(second.Value.NextCursor);
		Assert.Equal(3, first.Value.Boundaries.Count + second.Value.Boundaries.Count);

		// A limit outside 1..25 is a malformed request, not something to clamp silently.
		SyncResult<SyncBoundaryPage> bad = await context.SyncSessions(db).GetBoundaryPageAsync(
			owner.Principal, new SyncBoundaryPageRequest(SyncProtocol.Version, handshake.SyncSessionId, handshake.BoundaryCursor!, 26));
		Assert.Equal(SyncErrorCode.InvalidRequest, bad.Failure!.Code);
	}

	// ---------------------------------------------------------------- history pages

	[Fact]
	public async Task HistoryPage_DeliversTheWholeEncryptedMessage_WithAKeyThatDecryptsAndAnAuthorKeyThatVerifies()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		Accepted sent = await SendAsync(context, db, group.Founder, group.Chat.Id, "readable history");

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-history");
		SyncBoundary boundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Second, handshake), b => b.Scope!.ChatId == group.Chat.Id.ToString());

		SyncResult<SyncPage> page = await context.SyncPages(db).ReadHistoryAsync(
			group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, handshake.SyncSessionId, boundary, null, 50));

		Assert.Null(page.Failure);
		Assert.True(page.Value!.Complete);
		SyncChange message = Assert.Single(page.Value.Changes, c => c.Kind == ChangeKind.Message);
		Assert.Equal(sent.Message.MessageId, message.Message!.Message.MessageId);
		Assert.Equal(boundary.Scope, message.Scope);

		// The granted key really opens the delivered package, and the delivered author key really
		// verifies its signature - the page hands over a readable message, not a reference to one.
		Assert.True(Base64Url.TryDecode(message.Message.Key.MessageKey, out byte[] messageKey));
		string[] parts = message.Message.Message.SignedPackage.Split('.');
		Assert.True(Base64Url.TryDecode(parts[1], out byte[] payload));
		using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payload);
		System.Text.Json.JsonElement content = document.RootElement.GetProperty("content");
		Assert.True(Base64Url.TryDecode(content.GetProperty("nonce").GetString(), out byte[] nonce));
		Assert.True(Base64Url.TryDecode(content.GetProperty("ciphertext").GetString(), out byte[] ciphertext));
		Assert.True(Base64Url.TryDecode(content.GetProperty("tag").GetString(), out byte[] tag));

		byte[] plaintext = new byte[ciphertext.Length];
		using (AesGcm aes = new(messageKey, 16))
		{
			aes.Decrypt(nonce, ciphertext, tag, plaintext, TestAuthorKey.Aad(sent.Binding));
		}

		Assert.Equal(sent.Plaintext, Encoding.UTF8.GetString(plaintext));

		Assert.True(Base64Url.TryDecode(message.Message.AuthorVerificationKey.Spki, out byte[] spki));
		Assert.Equal(sent.Spki, spki);
		using ECDsa verifier = ECDsa.Create();
		verifier.ImportSubjectPublicKeyInfo(spki, out _);
		Assert.True(Base64Url.TryDecode(parts[2], out byte[] signature));
		Assert.True(verifier.VerifyData(
			Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature,
			HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

		// A completed content enumeration owes the scope's own metadata as well.
		Assert.Contains(page.Value.Changes, c => c.Kind == ChangeKind.Unread && c.Unread!.MembershipId == boundary.Scope!.MembershipId);
		Assert.Contains(page.Value.Changes, c => c.Kind == ChangeKind.Membership);
		Assert.Contains(page.Value.Changes, c => c.Kind == ChangeKind.MembershipEvent && c.MembershipEvent!.Kind == MembershipEventKind.Added);
		Assert.Equal(boundary.Through, page.Value.AppliedThrough);
	}

	[Fact]
	public async Task HistoryPaging_NeitherSkipsNorRepeats_WhileNewMessagesKeepArriving()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		List<string> beforeHandshake = [];
		for (int i = 0; i < 5; i++)
		{
			beforeHandshake.Add((await SendAsync(context, db, group.Founder, group.Chat.Id, $"m{i}")).Message.MessageId);
		}

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-paging");
		SyncBoundary boundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Second, handshake), b => b.Scope!.ChatId == group.Chat.Id.ToString());

		List<string> read = [];
		string? cursor = null;
		int pages = 0;
		while (true)
		{
			SyncResult<SyncPage> page = await context.SyncPages(db).ReadHistoryAsync(
				group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, handshake.SyncSessionId, boundary, cursor, 2));

			Assert.Null(page.Failure);
			read.AddRange(page.Value!.Changes.Where(c => c.Kind == ChangeKind.Message).Select(c => c.Message!.Message.MessageId));

			// A message committed WHILE the enumeration runs lands above the fixed boundary and is
			// therefore outside this view - it can neither appear here nor shift the page that follows.
			await SendAsync(context, db, group.Founder, group.Chat.Id, $"concurrent-{pages}");

			Assert.Equal(page.Value.Complete, page.Value.NextCursor is null);
			cursor = page.Value.NextCursor;
			pages++;
			if (page.Value.Complete)
			{
				break;
			}

			Assert.True(pages < 10, "The enumeration must advance or terminate.");
		}

		Assert.Equal(beforeHandshake, read);

		// The same cursor replayed after everything: the saved page, not a shifted one.
		SyncResult<SyncPage> replay = await context.SyncPages(db).ReadHistoryAsync(
			group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, handshake.SyncSessionId, boundary, null, 2));
		Assert.Null(replay.Failure);
		Assert.Equal(
			beforeHandshake.Take(2),
			replay.Value!.Changes.Where(c => c.Kind == ChangeKind.Message).Select(c => c.Message!.Message.MessageId));
	}

	[Fact]
	public async Task HistoryPage_OfAFrozenArchive_StopsAtTheFreezeAndAnswersFromItsOwnSnapshot()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		Accepted archived = await SendAsync(context, db, group.Founder, group.Chat.Id, "inside the archive");

		Result<ChatMembershipChange> removed = await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId);
		Assert.True(removed.IsSuccess);

		// Everything after the removal belongs to the chat, never to the archive.
		Accepted afterwards = await SendAsync(context, db, group.Founder, group.Chat.Id, "after the removal");

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-archive");
		SyncBoundary boundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Second, handshake), b => b.Scope!.ChatId == group.Chat.Id.ToString());

		SyncResult<SyncPage> page = await context.SyncPages(db).ReadHistoryAsync(
			group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, handshake.SyncSessionId, boundary, null, 50));

		Assert.Null(page.Failure);
		List<string> delivered = [.. page.Value!.Changes.Where(c => c.Kind == ChangeKind.Message).Select(c => c.Message!.Message.MessageId)];
		Assert.Contains(archived.Message.MessageId, delivered);
		Assert.DoesNotContain(afterwards.Message.MessageId, delivered);

		SyncChange membership = Assert.Single(page.Value.Changes, c => c.Kind == ChangeKind.Membership);
		Assert.Equal(MembershipState.Frozen, membership.Membership!.State);
		// "Resolve archive metadata/status at the named snapshot, not today's state."
		Assert.Equal(boundary.Scope!.MembershipId, membership.Membership.MetadataSnapshotId);
		Assert.All(membership.Membership.AllowedRanges, range => Assert.NotNull(range.FrozenAtChangeSequence));

		// The removed subject learns that it was removed, never by whom.
		SyncChange removal = Assert.Single(
			page.Value.Changes, c => c.Kind == ChangeKind.MembershipEvent && c.MembershipEvent!.Kind == MembershipEventKind.Removed);
		Assert.Equal(group.Second.Account.Id, removal.MembershipEvent!.SubjectAccountContextId);
		Assert.Null(removal.MembershipEvent.ActorAccountContextId);

		// The statuses of an archived message come from the freeze's own copies.
		await using AppDbContext read = context.CreateContext();
		Assert.True(await read.SyncMembershipMessageStatusSnapshots
			.AnyAsync(s => s.MembershipId == boundary.Scope.MembershipId && s.MessageId == archived.Message.MessageId));
	}

	[Fact]
	public async Task FrozenArchive_OfAChatWithOnlyV1Messages_AnswersWithItsOwnStatuses_IncludingWhoNeverReported()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		Accepted first = await SendAsync(context, db, group.Founder, group.Chat.Id, "first");

		// The one reachable way a v1 recipient state exists today: the Third person's OWN send marks the
		// older incoming message logically Seen with reason OwnSend. So at the freeze, one entitled person
		// has reported something about "first" and one (the departing member) has not.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Accepted second = await SendAsync(context, db, group.Third, group.Chat.Id, "second");

		// The precondition that makes this archive purely sequence-space: stage 11 acceptance writes
		// SyncMessages only, so this chat has no legacy Messages row the time-addressed pass could find.
		Assert.False(await db.Messages.AsNoTracking().AnyAsync(m => m.ChatId == group.Chat.Id));

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Result<ChatMembershipChange> removed = await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId);
		Assert.True(removed.IsSuccess);

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Accepted afterwards = await SendAsync(context, db, group.Founder, group.Chat.Id, "after the removal");

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-v1-archive");
		SyncBoundary boundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Second, handshake), b => b.Scope!.ChatId == group.Chat.Id.ToString());

		SyncResult<SyncPage> page = await context.SyncPages(db).ReadHistoryAsync(
			group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, handshake.SyncSessionId, boundary, null, 50));

		Assert.Null(page.Failure);
		List<string> delivered = [.. page.Value!.Changes.Where(c => c.Kind == ChangeKind.Message).Select(c => c.Message!.Message.MessageId)];
		Assert.Equal(new[] { first.Message.MessageId, second.Message.MessageId }, delivered);

		List<MessageRecipientState> states =
			[.. page.Value.Changes.Where(c => c.Kind == ChangeKind.RecipientState).Select(c => c.RecipientState!)];

		// Without the freeze's own copies this list is empty: the live rows belong to the chat the archive
		// has left, and "Resolve archive metadata/status at the named snapshot" would deliver the message
		// with nobody having received or seen it.
		MessageRecipientState reported = Assert.Single(
			states, s => s.MessageId == first.Message.MessageId && s.RecipientAccountContextId == group.Third.Account.Id);
		Assert.Equal(SeenReason.OwnSend, reported.Seen!.Reason);

		// The archive's own DENOMINATOR: entitled to the message, never reported anything about it.
		MessageRecipientState silent = Assert.Single(
			states, s => s.MessageId == first.Message.MessageId && s.RecipientAccountContextId == group.Second.Account.Id);
		Assert.Null(silent.ReceivedAtUtc);
		Assert.Null(silent.Seen);

		// Bounded by the ranges the freeze closed: nothing committed after the exit is in the archive.
		Assert.DoesNotContain(states, s => s.MessageId == afterwards.Message.MessageId);
		await using AppDbContext read = context.CreateContext();
		Assert.False(await read.SyncMembershipMessageStatusSnapshots
			.AnyAsync(s => s.MembershipId == boundary.Scope!.MembershipId && s.MessageId == afterwards.Message.MessageId));
	}

	[Fact]
	public async Task ANewGrantForOldHistory_InvalidatesTheFixedView_AndMakesThatOldPeriodDownloadable()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		Accepted old = await SendAsync(context, db, group.Founder, group.Chat.Id, "older than the outsider");

		// The outsider holds nothing in this chat yet, and its session is fixed on exactly that view.
		SyncHandshakeResponse before = await HandshakeAsync(context, db, group.Outsider, "conn-grant");
		Assert.Empty(await AllBoundariesAsync(context, db, group.Outsider, before));

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Result<ChatMembershipChange> added = await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.Last24Hours, 0));
		Assert.True(added.IsSuccess);

		// "Ново разрешение за стара история задейства сваляне независимо от текущия курсор": the fixed
		// view is no longer the current one, whatever position the device had reached.
		SyncResult<SyncPage> stale = await context.SyncPages(db).ReadCatalogAsync(
			group.Outsider.Principal, new SyncPageRequest(SyncProtocol.Version, before.SyncSessionId, before.CatalogBoundary, null, 50));
		Assert.Equal(SyncErrorCode.RightsChanged, stale.Failure!.Code);

		// The new handshake fixes a set that CONTAINS the newly permitted old period, and the content is
		// downloadable from its start rather than from where the device had got to.
		SyncHandshakeResponse after = await HandshakeAsync(context, db, group.Outsider, "conn-grant-2");
		Assert.NotEqual(before.RightsVersion, after.RightsVersion);

		SyncBoundary boundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Outsider, after), b => b.Scope!.ChatId == group.Chat.Id.ToString());

		SyncResult<SyncPage> page = await context.SyncPages(db).ReadHistoryAsync(
			group.Outsider.Principal, new SyncPageRequest(SyncProtocol.Version, after.SyncSessionId, boundary, null, 50));

		Assert.Null(page.Failure);
		Assert.Contains(page.Value!.Changes, c => c.Kind == ChangeKind.Message && c.Message!.Message.MessageId == old.Message.MessageId);
		SyncChange membership = Assert.Single(page.Value.Changes, c => c.Kind == ChangeKind.Membership);
		Assert.NotEmpty(membership.Membership!.AllowedRanges);
	}

	[Fact]
	public async Task Page_AfterTheCallersOwnRightsChange_AnswersRightsChanged()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await SendAsync(context, db, group.Founder, group.Chat.Id, "before the change");

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-rights");
		SyncBoundary boundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Second, handshake), b => b.Scope!.ChatId == group.Chat.Id.ToString());

		// This caller's OWN access changes - "RightsChanged requires new handshake/readiness".
		Result<ChatMembershipChange> removed = await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId);
		Assert.True(removed.IsSuccess);

		SyncResult<SyncPage> page = await context.SyncPages(db).ReadHistoryAsync(
			group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, handshake.SyncSessionId, boundary, null, 50));
		Assert.Equal(SyncErrorCode.RightsChanged, page.Failure!.Code);

		SyncResult<SyncBoundaryPage> boundaries = await context.SyncSessions(db).GetBoundaryPageAsync(
			group.Second.Principal, new SyncBoundaryPageRequest(SyncProtocol.Version, handshake.SyncSessionId, handshake.BoundaryCursor!, 5));
		Assert.Equal(SyncErrorCode.RightsChanged, boundaries.Failure!.Code);

		// A new handshake fixes the new view - now an archive - and reading continues inside it.
		SyncHandshakeResponse again = await HandshakeAsync(context, db, group.Second, "conn-rights-2");
		Assert.NotEqual(handshake.RightsVersion, again.RightsVersion);
		SyncBoundary fresh = Assert.Single(
			await AllBoundariesAsync(context, db, group.Second, again), b => b.Scope!.ChatId == group.Chat.Id.ToString());
		SyncResult<SyncPage> after = await context.SyncPages(db).ReadHistoryAsync(
			group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, again.SyncSessionId, fresh, null, 50));
		Assert.Null(after.Failure);
	}

	[Fact]
	public async Task Page_WithAnUnknownOrForeignCursor_AnswersCursorInvalid()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await SendAsync(context, db, group.Founder, group.Chat.Id, "content");

		SyncHandshakeResponse mine = await HandshakeAsync(context, db, group.Second, "conn-mine");
		SyncBoundary boundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Second, mine), b => b.Scope!.ChatId == group.Chat.Id.ToString());

		SyncResult<SyncPage> unknown = await context.SyncPages(db).ReadHistoryAsync(
			group.Second.Principal,
			new SyncPageRequest(SyncProtocol.Version, mine.SyncSessionId, boundary, Base64Url.Encode(RandomNumberGenerator.GetBytes(32)), 50));
		Assert.Equal(SyncErrorCode.CursorInvalid, unknown.Failure!.Code);

		// Another member's cursor, presented by this caller: not this session's, and therefore not usable.
		SyncHandshakeResponse theirs = await HandshakeAsync(context, db, group.Third, "conn-theirs");
		SyncResult<SyncBoundaryPage> foreignBoundary = await context.SyncSessions(db).GetBoundaryPageAsync(
			group.Second.Principal, new SyncBoundaryPageRequest(SyncProtocol.Version, mine.SyncSessionId, theirs.BoundaryCursor!, 5));
		Assert.Equal(SyncErrorCode.CursorInvalid, foreignBoundary.Failure!.Code);

		// Another member's SESSION, presented by this caller: not this caller's session at all.
		SyncResult<SyncPage> foreignSession = await context.SyncPages(db).ReadHistoryAsync(
			group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, theirs.SyncSessionId, boundary, null, 50));
		Assert.Equal(SyncErrorCode.SessionExpired, foreignSession.Failure!.Code);
	}

	// ---------------------------------------------------------------- catalog and delta

	[Fact]
	public async Task CatalogPage_InFullMode_AnswersEveryFixedScopeWithItsMetadataAndUnread()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await SendAsync(context, db, group.Founder, group.Chat.Id, "one");

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-catalog");

		SyncResult<SyncPage> page = await context.SyncPages(db).ReadCatalogAsync(
			group.Second.Principal,
			new SyncPageRequest(SyncProtocol.Version, handshake.SyncSessionId, handshake.CatalogBoundary, null, 50));

		Assert.Null(page.Failure);
		Assert.True(page.Value!.Complete);
		SyncChange chat = Assert.Single(page.Value.Changes, c => c.Kind == ChangeKind.Chat && c.Chat!.ChatId == group.Chat.Id.ToString());
		Assert.Equal(ChatKind.Group, chat.Chat!.Kind);
		Assert.Contains(group.Founder.Account.Id, chat.Chat.MemberAccountContextIds);
		Assert.Contains(group.Second.Account.Id, chat.Chat.MemberAccountContextIds);
		Assert.Equal("1", chat.Chat.Unread.Count);
		Assert.NotNull(chat.Chat.LastMessageId);
		Assert.Single(chat.Chat.Memberships);
		Assert.Equal(handshake.CatalogBoundary.Through, page.Value.AppliedThrough);
	}

	[Fact]
	public async Task DeltaPage_WalksTheJournalFromTheUploadedPosition_AndRefusesBelowThePrunedWatermark()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await SendAsync(context, db, group.Founder, group.Chat.Id, "already applied");

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-delta");
		SyncBoundary boundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Second, handshake), b => b.Scope!.ChatId == group.Chat.Id.ToString());

		// The device says what it already holds; the delta continues from exactly there.
		long applied = long.Parse(boundary.Through.Sequence);
		SyncResult<SyncPositionBatchResult> resume = await context.SyncSessions(db).ExchangePositionsAsync(
			group.Second.Principal, "conn-delta",
			new SyncPositionBatch(
				SyncProtocol.Version, handshake.SyncSessionId, "conn-delta", handshake.RightsVersion,
				"exchange-resume", PositionExchangeKind.Resume, 0,
				[new StreamPosition(StreamKind.Chat, boundary.Scope!.ChatId, boundary.Scope.MembershipId, applied.ToString())], true));
		Assert.Null(resume.Failure);

		SyncResult<SyncPage> caughtUp = await context.SyncPages(db).ReadDeltaAsync(
			group.Second.Principal, new SyncPageRequest(SyncProtocol.Version, handshake.SyncSessionId, boundary, null, 50));
		Assert.Null(caughtUp.Failure);
		Assert.True(caughtUp.Value!.Complete);
		Assert.Empty(caughtUp.Value.Changes);

		// A device that has applied nothing walks the journal from its beginning and sees the message.
		SyncHandshakeResponse behind = await HandshakeAsync(context, db, group.Third, "conn-behind");
		SyncBoundary behindBoundary = Assert.Single(
			await AllBoundariesAsync(context, db, group.Third, behind), b => b.Scope!.ChatId == group.Chat.Id.ToString());
		SyncResult<SyncPage> journal = await context.SyncPages(db).ReadDeltaAsync(
			group.Third.Principal, new SyncPageRequest(SyncProtocol.Version, behind.SyncSessionId, behindBoundary, null, 50));
		Assert.Null(journal.Failure);
		Assert.Contains(journal.Value!.Changes, c => c.Kind == ChangeKind.Message);
		Assert.Equal(behindBoundary.Through.Sequence, journal.Value.AppliedThrough.Sequence);

		// Once the journal below that point has been cleaned, the same read cannot prove it would skip
		// nothing - so it is refused and the device reconciles instead.
		await using (AppDbContext maintenance = context.CreateContext())
		{
			await maintenance.SyncChatSequenceCounters
				.Where(c => c.ChatId == group.Chat.Id.ToString())
				.ExecuteUpdateAsync(set => set.SetProperty(c => c.PrunedThroughChangeSequence, applied));
		}

		SyncResult<SyncPage> pruned = await context.SyncPages(db).ReadDeltaAsync(
			group.Third.Principal, new SyncPageRequest(SyncProtocol.Version, behind.SyncSessionId, behindBoundary, null, 50));
		Assert.Equal(SyncErrorCode.CursorInvalid, pruned.Failure!.Code);

		// The full content surface is unaffected: it reads the entity tables, which is why the journal may
		// be cleaned at all.
		SyncResult<SyncPage> history = await context.SyncPages(db).ReadHistoryAsync(
			group.Third.Principal, new SyncPageRequest(SyncProtocol.Version, behind.SyncSessionId, behindBoundary, null, 50));
		Assert.Null(history.Failure);
		Assert.Contains(history.Value!.Changes, c => c.Kind == ChangeKind.Message);
	}

	// ---------------------------------------------------------------- position exchanges and readiness

	[Fact]
	public async Task PositionExchange_ResumeThenAcknowledge_MakesTheSessionReadyAndCompletesTheInitialRestore()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await SendAsync(context, db, group.Founder, group.Chat.Id, "content");

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-ready");
		List<SyncBoundary> boundaries = await AllBoundariesAsync(context, db, group.Second, handshake);
		ISyncSessionService sessions = context.SyncSessions(db);

		StreamPosition[] zeroes =
		[
			new StreamPosition(StreamKind.Catalog, null, null, "0"),
			.. boundaries.Select(b => new StreamPosition(StreamKind.Chat, b.Scope!.ChatId, b.Scope.MembershipId, "0")),
		];

		SyncResult<SyncPositionBatchResult> resume = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-ready",
			new SyncPositionBatch(SyncProtocol.Version, handshake.SyncSessionId, "conn-ready", handshake.RightsVersion,
				"ex-resume", PositionExchangeKind.Resume, 0, zeroes, true));

		Assert.Null(resume.Failure);
		Assert.True(resume.Value!.Complete);
		// "ServerPositions ... covers exactly the submitted streams, in order."
		Assert.Equal(zeroes.Length, resume.Value.ServerPositions.Count);
		Assert.Empty(resume.Value.DataReadyScopes);

		// Resume alone is not readiness.
		SyncResult<SyncReadiness> midway = await sessions.GetReadinessAsync(group.Second.Principal, "conn-ready", handshake.SyncSessionId);
		Assert.Null(midway.Failure);
		Assert.False(midway.Value!.LiveReady);
		Assert.False(midway.Value.InitialRestoreComplete);

		StreamPosition[] atBoundary =
		[
			new StreamPosition(StreamKind.Catalog, null, null, handshake.CatalogBoundary.Through.Sequence),
			.. boundaries.Select(b => b.Through),
		];

		SyncResult<SyncPositionBatchResult> acknowledged = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-ready",
			new SyncPositionBatch(SyncProtocol.Version, handshake.SyncSessionId, "conn-ready", handshake.RightsVersion,
				"ex-ack", PositionExchangeKind.Acknowledge, 0, atBoundary, true));

		Assert.Null(acknowledged.Failure);
		Assert.Equal(boundaries.Count, acknowledged.Value!.DataReadyScopes.Count);

		SyncResult<SyncReadiness> ready = await sessions.GetReadinessAsync(group.Second.Principal, "conn-ready", handshake.SyncSessionId);
		Assert.True(ready.Value!.LiveReady);
		// The initial restoration of THIS SQLite copy is complete, and survives a restart.
		Assert.True(ready.Value.InitialRestoreComplete);

		// Another connection is not this session's readiness.
		SyncResult<SyncReadiness> elsewhere = await sessions.GetReadinessAsync(group.Second.Principal, "conn-elsewhere", handshake.SyncSessionId);
		Assert.False(elsewhere.Value!.LiveReady);

		// And the next handshake of this local instance is a Delta one.
		SyncHandshakeResponse next = await HandshakeAsync(context, db, group.Second, "conn-ready-2");
		Assert.Equal(SyncMode.Delta, next.Mode);
	}

	[Fact]
	public async Task PositionExchange_IsIdempotentPerBatch_AndRefusesAChangedBatchAGapOrAnythingAfterFinal()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, "conn-exchange");
		ISyncSessionService sessions = context.SyncSessions(db);
		StreamPosition catalogZero = new(StreamKind.Catalog, null, null, "0");

		SyncPositionBatch first = new(
			SyncProtocol.Version, handshake.SyncSessionId, "conn-exchange", handshake.RightsVersion,
			"ex-1", PositionExchangeKind.Resume, 0, [catalogZero], false);

		SyncResult<SyncPositionBatchResult> answered = await sessions.ExchangePositionsAsync(group.Second.Principal, "conn-exchange", first);
		Assert.Null(answered.Failure);
		Assert.False(answered.Value!.Complete);

		// The identical retry returns the SAVED result - compared field by field, because a record's own
		// equality would compare its two list members by reference.
		SyncResult<SyncPositionBatchResult> retried = await sessions.ExchangePositionsAsync(group.Second.Principal, "conn-exchange", first);
		Assert.Null(retried.Failure);
		Assert.Equal(answered.Value.ExchangeId, retried.Value!.ExchangeId);
		Assert.Equal(answered.Value.Kind, retried.Value.Kind);
		Assert.Equal(answered.Value.BatchIndex, retried.Value.BatchIndex);
		Assert.Equal(answered.Value.Complete, retried.Value.Complete);
		Assert.Equal(answered.Value.ServerPositions, retried.Value.ServerPositions);
		Assert.Equal(answered.Value.DataReadyScopes, retried.Value.DataReadyScopes);

		// A different batch under the same (exchange, index) is refused rather than applied.
		SyncResult<SyncPositionBatchResult> changed = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-exchange", first with { Positions = [new StreamPosition(StreamKind.Catalog, null, null, "0"), catalogZero] });
		Assert.Equal(SyncErrorCode.InvalidRequest, changed.Failure!.Code);

		// A gap in the indices is refused.
		SyncResult<SyncPositionBatchResult> gap = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-exchange", first with { BatchIndex = 2 });
		Assert.Equal(SyncErrorCode.InvalidRequest, gap.Failure!.Code);

		// The final batch, then anything after it.
		SyncResult<SyncPositionBatchResult> final = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-exchange", first with { BatchIndex = 1, Final = true });
		Assert.Null(final.Failure);
		Assert.True(final.Value!.Complete);

		SyncResult<SyncPositionBatchResult> afterFinal = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-exchange", first with { BatchIndex = 2, Final = true });
		Assert.Equal(SyncErrorCode.InvalidRequest, afterFinal.Failure!.Code);

		// Another connection's claim about this session is not this session's exchange.
		SyncResult<SyncPositionBatchResult> otherConnection = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-elsewhere", first with { ExchangeId = "ex-2", ConnectionId = "conn-elsewhere" });
		Assert.Equal(SyncErrorCode.InvalidRequest, otherConnection.Failure!.Code);

		// A stale rights version is refused before anything is recorded.
		SyncResult<SyncPositionBatchResult> staleRights = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-exchange", first with { ExchangeId = "ex-3", RightsVersion = "999999" });
		Assert.Equal(SyncErrorCode.RightsChanged, staleRights.Failure!.Code);

		// A position whose ids are not the contract's id tokens ("IDs are case-sensitive ASCII tokens
		// matching [A-Za-z0-9_-]{1,128}") is refused outright. Accepting one would let a caller spell the
		// separators a batch's own identity is built from inside an id, so a CHANGED batch could be hashed
		// to an earlier one's identity and answered with its saved result instead of InvalidRequest.
		SyncResult<SyncPositionBatchResult> forgedIds = await sessions.ExchangePositionsAsync(
			group.Second.Principal, "conn-exchange",
			first with
			{
				ExchangeId = "ex-4",
				Positions = [new StreamPosition(StreamKind.Chat, "7:m;Chat:7", "m", "0")],
			});
		Assert.Equal(SyncErrorCode.InvalidRequest, forgedIds.Failure!.Code);
	}

	// ---------------------------------------------------------------- journal retention

	/// <summary>
	/// Runs the bounded sweep until it stops removing anything, and answers how much it removed in total.
	/// One call visits at most a fixed number of streams by design; this is how a test makes sure its own
	/// chat was among them without weakening that bound.
	/// </summary>
	private static async Task<int> SweepUntilQuietAsync(MembershipTestContext context, AppDbContext db, int maxSweeps = 25)
	{
		int removed = 0;
		for (int sweep = 0; sweep < maxSweeps; sweep++)
		{
			JournalPruneOutcome outcome = await context.SyncRetention(db).PruneJournalsAsync();
			removed += outcome.ChatChangesRemoved;
			if (outcome.ChatChangesRemoved == 0 && outcome.CatalogEntriesRemoved == 0)
			{
				break;
			}
		}

		return removed;
	}

	[Fact]
	public async Task JournalRetention_KeepsWhatALiveSessionStillNeeds_AndRaisesTheWatermarkForWhatItRemoves()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await SendAsync(context, db, group.Founder, group.Chat.Id, "old");

		string chatId = group.Chat.Id.ToString();
		ISyncRetention retention = context.SyncRetention(db);

		// Nothing of this chat is past the horizon yet, so nothing of it is pruned.
		await retention.PruneJournalsAsync();
		await using (AppDbContext read = context.CreateContext())
		{
			Assert.True(await read.SyncChatChanges.AnyAsync(c => c.ChatId == chatId));
		}

		// Far past the retention horizon, but a live session of this chat has acknowledged nothing: the
		// journal stays exactly where it is.
		SyncHandshakeResponse handshake = await HandshakeAsync(context, db, group.Second, $"conn-retention-{Guid.NewGuid():N}");
		context.Clock.Advance(TimeSpan.FromDays(40));

		// Swept repeatedly on purpose: one sweep is a BOUNDED portion of the streams over the horizon, and
		// this durable test database holds other suites' chats too - so the chat under test is only
		// certainly visited after the sweeps stop finding anything new.
		await SweepUntilQuietAsync(context, db);
		await using (AppDbContext read = context.CreateContext())
		{
			Assert.True(await read.SyncChatChanges.AnyAsync(c => c.ChatId == chatId));
			Assert.Equal(0, await read.SyncChatSequenceCounters.Where(c => c.ChatId == chatId).Select(c => c.PrunedThroughChangeSequence).SingleAsync());
		}

		// Once that session has expired, nothing live pins the journal any more. Bounded per call, like
		// the journal sweep, so this repeats until this test's own session is gone.
		context.Clock.Advance(TimeSpan.FromDays(2));
		int purged = 0;
		for (int sweep = 0; sweep < 25; sweep++)
		{
			purged += await context.SyncRetention(db).PurgeExpiredSessionsAsync();
			await using AppDbContext probe = context.CreateContext();
			if (!await probe.SyncSessions.AnyAsync(s => s.Id == handshake.SyncSessionId))
			{
				break;
			}
		}

		Assert.True(purged > 0);
		await using (AppDbContext read = context.CreateContext())
		{
			Assert.False(await read.SyncSessions.AnyAsync(s => s.Id == handshake.SyncSessionId));
			// Cursors and boundaries follow the session they belong to.
			Assert.False(await read.SyncCursors.AnyAsync(c => c.SyncSessionId == handshake.SyncSessionId));
			Assert.False(await read.SyncSessionBoundaries.AnyAsync(b => b.SyncSessionId == handshake.SyncSessionId));
		}

		int removed = await SweepUntilQuietAsync(context, db);
		Assert.True(removed > 0);

		await using (AppDbContext read = context.CreateContext())
		{
			Assert.False(await read.SyncChatChanges.AnyAsync(c => c.ChatId == chatId));
			// The watermark moved in the same transaction, so a delta below it is refused rather than
			// answered with a hole - and the messages themselves are untouched.
			Assert.True(await read.SyncChatSequenceCounters.Where(c => c.ChatId == chatId).Select(c => c.PrunedThroughChangeSequence).SingleAsync() > 0);
			Assert.True(await read.SyncMessages.AnyAsync(m => m.ChatId == chatId));
		}
	}
}
