using System.Text.Json;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Delivery;
using Services.Delivery.Models;
using Services.Membership.Models;
using Services.Messaging.Models;
using Services.Statuses.Models;
using Services.Sync.Contracts;
using Services.Sync.Models;
using Services.Sync.Service;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// The v1 receipt surface (docs/sync-receipts.md) against a real PostgreSQL and the real services: a
/// device proves what IT holds of a v1 message, the person's public mark follows from the first such
/// proof, a frozen archive moves private progress only, Seen is refused, and the status changes this
/// produces are delivered by kind - the public one to everyone still entitled to the message, the
/// private one to the exact SQLite copy that reported it and to nothing else, and nothing at all beyond
/// a frozen archive's fixed bound.
///
/// Two of the cases below are about the PUBLIC state being one fact per person: the stored rows are per
/// stint, a rejoin legitimately produces a second one, and every reader answers from the same fold.
///
/// Nothing here is a stand-in except the SignalR sink and the broker, which a test process cannot have.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SyncReceiptTests(DatabaseFixture fixture)
{
	private static readonly JsonSerializerOptions SyncJson = SyncProtocol.CreateJsonOptions();

	// ---------------------------------------------------------------- helpers

	/// <summary>One accepted v1 message: exactly what the hub's v1 send commits.</summary>
	private sealed record Accepted(AcceptedMessage Message, long OutboxId);

	private static async Task<Accepted> SendAsync(
		MembershipTestContext context, AppDbContext db, TestMember sender, int legacyChatId,
		string clientOperationId = "op-1", string text = "hello")
	{
		using TestAuthorKey key = new();
		AuthorKeyReference reference = new(
			sender.Account.Id, legacyChatId.ToString(), $"authkey-{Guid.NewGuid():N}", "1");

		Assert.True((await context.AuthorKeys(db).RegisterAsync(sender.Principal, reference, key.Spki)).IsSuccess);

		string chatId = legacyChatId.ToString();
		SyncMembershipEntity stint = await StintAsync(db, chatId, sender.Account.Id, SyncMembershipState.Active);

		MessageBinding binding = new(
			SyncProtocol.Version,
			new OperationIdentity(new DeviceContext(sender.Account.Id, sender.Device.Id, sender.Instance.Id), clientOperationId),
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
		Assert.NotNull(result.Data.OutboxId);

		return new Accepted(accepted.Result, result.Data.OutboxId!.Value);
	}

	private static Task<SyncMembershipEntity> StintAsync(
		AppDbContext db, string chatId, string accountId, SyncMembershipState state) =>
		db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chatId && m.AccountId == accountId && m.State == state);

	/// <summary>A receipt exactly as the device builds it: its own identity, the stint it is reporting under.</summary>
	private static ReceiptRequest Receipt(
		TestMember device, SyncMembershipEntity stint, string messageId, ReceiptKind kind,
		string clientOperationId = "rcpt-1", long? rightsVersion = null, SeenObservation? observation = null) =>
		new(SyncProtocol.Version,
			new OperationIdentity(new DeviceContext(device.Account.Id, device.Device.Id, device.Instance.Id), clientOperationId),
			new MembershipScope(stint.ChatId, stint.Id, (rightsVersion ?? stint.RightsVersion).ToString()),
			messageId,
			kind,
			observation);

	private static Task<SyncMessageDeviceStateEntity?> DeviceStateAsync(AppDbContext db, string messageId, TestMember device) =>
		db.SyncMessageDeviceStates.AsNoTracking().FirstOrDefaultAsync(
			s => s.MessageId == messageId
				&& s.DeviceRegistrationId == device.Device.Id
				&& s.LocalInstanceId == device.Instance.Id);

	private static Task<List<SyncMessageRecipientStateEntity>> RecipientStatesAsync(
		AppDbContext db, string messageId, TestMember person) =>
		db.SyncMessageRecipientStates.AsNoTracking()
			.Where(s => s.MessageId == messageId && s.AccountId == person.Account.Id)
			.ToListAsync();

	private static Task<List<SyncOutboxEntity>> OutboxAsync(AppDbContext db, string messageId, SyncChangeKind kind) =>
		db.SyncOutbox.AsNoTracking()
			.Where(o => o.MessageId == messageId && o.ChangeKind == kind)
			.OrderBy(o => o.Id)
			.ToListAsync();

	private static DeliveryTask TaskFor(SyncOutboxEntity row) => new(
		SyncProtocol.Version,
		row.Id,
		row.ChatId,
		row.MessageId,
		row.ChangeSequence.ToString(),
		DeliveryEventId.ForChatChange(row.ChatId, row.ChangeSequence));

	private static LiveEvent EventOf(SentEvent sent)
	{
		Assert.Equal("sync-change", sent.Event);
		return JsonSerializer.Deserialize<LiveEvent>(Assert.IsType<string>(sent.Payload), SyncJson)!;
	}

	// ---------------------------------------------------------------- accepting a receipt

	[Fact]
	public async Task ReceivedReceipt_RecordsDeviceAndPersonOnce_AndARepeatIsAlreadyApplied()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);

		SyncResult<ReceiptAcceptance> first = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal, Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received));

		Assert.True(first.IsSuccess);
		Assert.Equal(ReceiptDisposition.Applied, first.Value!.Result.Disposition);
		Assert.NotNull(first.Value.Result.StateVersion);
		Assert.True(first.Value.QueuedForDelivery);

		// A readable copy implies the bytes are on disk, so Received back-fills the earlier step.
		SyncMessageDeviceStateEntity? device = await DeviceStateAsync(db, accepted.Message.MessageId, group.Second);
		Assert.NotNull(device);
		Assert.NotNull(device.CiphertextStoredAtUtc);
		Assert.NotNull(device.ReceivedAtUtc);
		Assert.Null(device.ObservedSeenAtUtc);

		SyncMessageRecipientStateEntity person = Assert.Single(
			await RecipientStatesAsync(db, accepted.Message.MessageId, group.Second));
		Assert.NotNull(person.ReceivedAtUtc);
		// Received is not an observation: no Seen, no reason, and nothing for unread to recompute.
		Assert.Null(person.SeenAtUtc);
		Assert.Null(person.SeenReason);

		long unreadBefore = (await db.SyncUnreadStates.AsNoTracking().SingleAsync(u => u.MembershipId == stint.Id)).Count;

		// The retry after a lost answer: the same disposition's idempotent twin, the same version, and no
		// second row anywhere.
		SyncResult<ReceiptAcceptance> again = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal,
			Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received, clientOperationId: "rcpt-2"));

		Assert.Equal(ReceiptDisposition.AlreadyApplied, again.Value!.Result.Disposition);
		Assert.Equal(first.Value.Result.StateVersion, again.Value.Result.StateVersion);
		Assert.False(again.Value.QueuedForDelivery);
		Assert.Single(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Second));
		Assert.Equal(
			unreadBefore,
			(await db.SyncUnreadStates.AsNoTracking().SingleAsync(u => u.MembershipId == stint.Id)).Count);

		// One journal entry and one outbox row per committed change - never one per reported receipt.
		Assert.Single(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.RecipientState));
		Assert.Single(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.DeviceState));
	}

	[Fact]
	public async Task StoredCiphertextReceipt_IsPrivateProgressOnly()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);

		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal, Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.CiphertextStored));

		Assert.Equal(ReceiptDisposition.Applied, result.Value!.Result.Disposition);

		// "Записването само на ciphertext се отчита отделно": stored bytes are not readiness.
		SyncMessageDeviceStateEntity? device = await DeviceStateAsync(db, accepted.Message.MessageId, group.Second);
		Assert.NotNull(device);
		Assert.NotNull(device.CiphertextStoredAtUtc);
		Assert.Null(device.ReceivedAtUtc);
		Assert.Empty(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Second));
		Assert.Empty(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.RecipientState));
	}

	[Fact]
	public async Task ReceiptForOwnMessage_IsPrivateProgressOnly()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Founder.Account.Id, SyncMembershipState.Active);

		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Founder.Principal, Receipt(group.Founder, stint, accepted.Message.MessageId, ReceiptKind.Received));

		Assert.Equal(ReceiptDisposition.Applied, result.Value!.Result.Disposition);
		Assert.NotNull(await DeviceStateAsync(db, accepted.Message.MessageId, group.Founder));

		// An author is not its own recipient and is excluded from every proportion.
		Assert.Empty(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Founder));
	}

	[Fact]
	public async Task ReceiptUnderAFrozenStint_MovesPrivateProgressOnly()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		SyncMembershipEntity frozen = await StintAsync(
			db, accepted.Message.ChatId, group.Third.Account.Id, SyncMembershipState.Frozen);

		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Third.Principal, Receipt(group.Third, frozen, accepted.Message.MessageId, ReceiptKind.Received));

		// The archive is downloadable and confirmable - that is what the private record is for - but
		// "Публичните отметки и unread в замразения чат остават непроменени".
		Assert.Equal(ReceiptDisposition.Applied, result.Value!.Result.Disposition);
		Assert.NotNull(await DeviceStateAsync(db, accepted.Message.MessageId, group.Third));
		Assert.Empty(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Third));
		Assert.Empty(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.RecipientState));
	}

	[Fact]
	public async Task ReceiptAfterARejoinWhoseSharedPeriodOverlaps_StillPublishesThePersonsReceived()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		// Removed, then the archive is confirmed by that device: private progress exists and no public
		// mark does.
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		SyncMembershipEntity frozen = await StintAsync(
			db, accepted.Message.ChatId, group.Third.Account.Id, SyncMembershipState.Frozen);
		Assert.Equal(
			ReceiptDisposition.Applied,
			(await context.Statuses(db).AcceptSyncReceiptAsync(
				group.Third.Principal, Receipt(group.Third, frozen, accepted.Message.MessageId, ReceiptKind.Received)))
				.Value!.Result.Disposition);
		Assert.Empty(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Third));

		// Re-added with a period that covers the same message again.
		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Third.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.LastTenDays, 0))).IsSuccess);

		SyncMembershipEntity rejoined = await StintAsync(
			db, accepted.Message.ChatId, group.Third.Account.Id, SyncMembershipState.Active);

		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Third.Principal,
			Receipt(group.Third, rejoined, accepted.Message.MessageId, ReceiptKind.Received, clientOperationId: "rcpt-rejoin"));

		// The device row is already complete, so the private half changes nothing - and the public mark
		// the newly active stint owes must still be written. Losing it here is exactly the overlap bug:
		// the author would never see this person as having received the message.
		Assert.Equal(ReceiptDisposition.Applied, result.Value!.Result.Disposition);
		SyncMessageRecipientStateEntity person = Assert.Single(
			await RecipientStatesAsync(db, accepted.Message.MessageId, group.Third));
		Assert.Equal(rejoined.Id, person.MembershipId);
		Assert.NotNull(person.ReceivedAtUtc);

		// The private row still names the stint its FIRST receipt was obtained under: the rejoin does not
		// move the old archive's progress.
		SyncMessageDeviceStateEntity? device = await DeviceStateAsync(db, accepted.Message.MessageId, group.Third);
		Assert.NotNull(device);
		Assert.Equal(frozen.Id, device.MembershipId);
	}

	[Fact]
	public async Task ReceiptForAMessageOutsideTheNamedStintsGrants_IsTerminallyNotApplicable()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		// Added afterwards with no previous history: this stint's grants start after the message.
		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId])).IsSuccess);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Outsider.Account.Id, SyncMembershipState.Active);

		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Outsider.Principal, Receipt(group.Outsider, stint, accepted.Message.MessageId, ReceiptKind.Received));

		// Terminal, with no StateVersion: the device's queue drops it instead of retrying forever, and
		// nothing is written about a message this stint may not read.
		Assert.True(result.IsSuccess);
		Assert.Equal(ReceiptDisposition.NotApplicable, result.Value!.Result.Disposition);
		Assert.Null(result.Value.Result.StateVersion);
		Assert.False(result.Value.QueuedForDelivery);
		Assert.Null(await DeviceStateAsync(db, accepted.Message.MessageId, group.Outsider));
		Assert.Empty(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Outsider));
	}

	[Fact]
	public async Task ReceiptOfAnUnknownMessage_IsTerminallyNotApplicable()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, group.Chat.Id.ToString(), group.Second.Account.Id, SyncMembershipState.Active);

		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal, Receipt(group.Second, stint, Guid.NewGuid().ToString("D"), ReceiptKind.Received));

		Assert.Equal(ReceiptDisposition.NotApplicable, result.Value!.Result.Disposition);
		Assert.Null(result.Value.Result.StateVersion);
	}

	// ---------------------------------------------------------------- rights, identity and shape

	[Fact]
	public async Task ReceiptWithAStaleRightsVersion_IsStillApplied_ButAHigherOneReconciles()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);

		// An offline receipt carries the rights version of its enqueue moment; the authority for whether
		// it still applies is the stint's grants, not that number.
		SyncResult<ReceiptAcceptance> stale = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal,
			Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.CiphertextStored, rightsVersion: 0));

		Assert.True(stale.IsSuccess);
		Assert.Equal(ReceiptDisposition.Applied, stale.Value!.Result.Disposition);

		// A version the server never issued is a diverged view: reconciled, never dropped.
		SyncResult<ReceiptAcceptance> ahead = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal,
			Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received,
				clientOperationId: "rcpt-ahead", rightsVersion: stint.RightsVersion + 10));

		Assert.False(ahead.IsSuccess);
		Assert.Equal(SyncErrorCode.RightsChanged, ahead.Failure!.Code);
		Assert.Null((await DeviceStateAsync(db, accepted.Message.MessageId, group.Second))!.ReceivedAtUtc);
	}

	[Fact]
	public async Task ReceiptNamingAnotherDevicesIdentity_IsRefusedAndWritesNothing()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);

		// The whole identity tuple or nothing: this is the caller's own session reporting under somebody
		// else's device. It must not be silently re-attributed to the caller's own instance.
		ReceiptRequest foreign = Receipt(group.Third, stint, accepted.Message.MessageId, ReceiptKind.Received);
		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal, foreign);

		Assert.False(result.IsSuccess);
		Assert.Equal(SyncErrorCode.InvalidRequest, result.Failure!.Code);
		Assert.Null(await DeviceStateAsync(db, accepted.Message.MessageId, group.Second));
		Assert.Null(await DeviceStateAsync(db, accepted.Message.MessageId, group.Third));

		// Same request, but only the local instance is somebody else's - still the whole tuple.
		ReceiptRequest wrongInstance = foreign with
		{
			Operation = new OperationIdentity(
				new DeviceContext(group.Second.Account.Id, group.Second.Device.Id, group.Third.Instance.Id), "rcpt-3"),
		};

		Assert.Equal(
			SyncErrorCode.InvalidRequest,
			(await context.Statuses(db).AcceptSyncReceiptAsync(group.Second.Principal, wrongInstance)).Failure!.Code);
	}

	[Fact]
	public async Task ReceiptFromARevokedDevice_IsTransientlyRefused_NotDropped()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);

		SyncDeviceRegistrationEntity revoked = await db.SyncDeviceRegistrations.SingleAsync(d => d.Id == group.Second.Device.Id);
		revoked.TrustState = DeviceTrustState.Revoked;
		revoked.RevokedAtUtc = context.Clock.GetUtcNow().UtcDateTime;
		revoked.TrustVersion += 1;
		await db.SaveChangesAsync();

		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal, Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received));

		// A SyncFailure, never NotApplicable: an authorization failure must not make a device drop a
		// receipt it genuinely owes.
		Assert.False(result.IsSuccess);
		Assert.Equal(SyncErrorCode.SessionExpired, result.Failure!.Code);
		Assert.Null(await DeviceStateAsync(db, accepted.Message.MessageId, group.Second));
	}

	[Fact]
	public async Task SeenAndObservations_AreRefusedByThisPart()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);

		// Seen fixes a time, a reason and an unread decrement, and the v1 row has no column for the
		// client's original instant yet: refused explicitly rather than half-applied.
		SyncResult<ReceiptAcceptance> seen = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal,
			Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Seen,
				observation: new SeenObservation(
					Services.Sessions.RfcTimestamp.Format(context.Clock.GetUtcNow().UtcDateTime), false, null, null)));

		Assert.False(seen.IsSuccess);
		Assert.Equal(SyncErrorCode.InvalidRequest, seen.Failure!.Code);

		// "Only Seen carries Observation; other receipt kinds require null."
		SyncResult<ReceiptAcceptance> observedReceived = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal,
			Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received,
				observation: new SeenObservation(
					Services.Sessions.RfcTimestamp.Format(context.Clock.GetUtcNow().UtcDateTime), false, null, null)));

		Assert.Equal(SyncErrorCode.InvalidRequest, observedReceived.Failure!.Code);

		// And an unsupported protocol version fails before any mutation at all.
		ReceiptRequest wrongVersion = Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received)
			with { ProtocolVersion = SyncProtocol.Version + 1 };

		Assert.Equal(
			SyncErrorCode.UnsupportedVersion,
			(await context.Statuses(db).AcceptSyncReceiptAsync(group.Second.Principal, wrongVersion)).Failure!.Code);

		Assert.Null(await DeviceStateAsync(db, accepted.Message.MessageId, group.Second));
		Assert.Empty(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Second));
	}

	[Fact]
	public async Task WithTheDeploymentGateOff_TheReceiptStillCommits_ButQueuesNoDelivery()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);

		DeliveryOptions gateOff = MembershipTestContext.DeliveryConfiguration(publishStatusChanges: false);
		SyncResult<ReceiptAcceptance> result = await context.Statuses(db, gateOff).AcceptSyncReceiptAsync(
			group.Second.Principal, Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received));

		// The default deployment state: an older consumer would deliver a status row as a full message,
		// so no row is queued - but the state, its journal entry and the answer still commit, and the
		// HTTP delta still carries the change.
		Assert.Equal(ReceiptDisposition.Applied, result.Value!.Result.Disposition);
		Assert.False(result.Value.QueuedForDelivery);
		Assert.NotNull(await DeviceStateAsync(db, accepted.Message.MessageId, group.Second));
		Assert.Single(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Second));
		Assert.Empty(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.RecipientState));
		Assert.Empty(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.DeviceState));

		string chatId = accepted.Message.ChatId;
		Assert.NotEmpty(await db.SyncChatChanges.AsNoTracking()
			.Where(c => c.ChatId == chatId
				&& c.ReferenceId == accepted.Message.MessageId
				&& c.Kind == SyncChangeKind.RecipientState)
			.ToListAsync());
	}

	// ---------------------------------------------------------------- delivering a status change

	[Fact]
	public async Task PublicStatusDelivery_ReachesEveryStillEntitledPerson_AndNoFrozenArchive()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);
		Assert.True((await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal, Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received))).IsSuccess);

		// The third member is removed AFTER the status was recorded: its archive is resolved at its own
		// snapshot and must not receive anything produced after the freeze.
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		SyncOutboxEntity row = Assert.Single(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.RecipientState));
		Assert.Equal(group.Second.Account.Id, row.SecondaryId);

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		DeliveryOutcome outcome = await context.Delivery(consumer).DeliverAsync(TaskFor(row));

		Assert.False(outcome.IsUnprocessable);
		Assert.Equal(2, outcome.DeviceSends);
		Assert.DoesNotContain(context.Hub.Sent, s => s.Audience.Contains($"sync-device:{group.Third.Device.Id}"));

		SentEvent toAuthor = Assert.Single(context.Hub.Sent, s => s.Audience.Single() == $"sync-device:{group.Founder.Device.Id}");
		LiveEvent live = EventOf(toAuthor);

		Assert.Equal(ChangeKind.RecipientState, live.Change.Kind);
		Assert.Equal(group.Founder.Account.Id, live.AccountContextId);
		Assert.Null(live.Change.Message);
		Assert.Null(live.Change.DeviceState);

		MessageRecipientState state = Assert.IsType<MessageRecipientState>(live.Change.RecipientState);
		Assert.Equal(accepted.Message.MessageId, state.MessageId);
		Assert.Equal(group.Second.Account.Id, state.RecipientAccountContextId);
		Assert.NotNull(state.ReceivedAtUtc);
		Assert.Null(state.Seen);

		// The canonical id of this fact, so a live event and a later delta page deduplicate.
		Assert.Equal(
			$"{accepted.Message.ChatId}:recipientState:{accepted.Message.MessageId}:{group.Second.Account.Id}:{state.Version}",
			live.Change.EventId);
	}

	[Fact]
	public async Task PrivateStatusDelivery_ReachesOnlyTheReportingLocalInstance()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		TestMember otherCopy = await context.AddDeviceAsync(db, group.Second);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);
		Assert.True((await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal,
			Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.CiphertextStored))).IsSuccess);

		SyncOutboxEntity row = Assert.Single(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.DeviceState));
		Assert.Equal(group.Second.Instance.Id, row.SecondaryId);
		// One id, never two concatenated: the column holds at most 100 characters.
		Assert.True(row.SecondaryId!.Length <= 100);

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		DeliveryOutcome outcome = await context.Delivery(consumer).DeliverAsync(TaskFor(row));

		Assert.Equal(1, outcome.DeviceSends);
		SentEvent sent = Assert.Single(context.Hub.Sent);

		// This SQLite copy and nothing else: not the person's other devices, not the author, and not even
		// the same registration's other local instance.
		Assert.Equal($"sync-instance:{group.Second.Instance.Id}", sent.Audience.Single());
		Assert.DoesNotContain(context.Hub.Sent, s => s.Audience.Contains($"sync-instance:{otherCopy.Instance.Id}"));
		Assert.DoesNotContain(context.Hub.Sent, s => s.Audience.Any(a => a.StartsWith("sync-device:", StringComparison.Ordinal)));

		LiveEvent live = EventOf(sent);
		Assert.Equal(ChangeKind.DeviceState, live.Change.Kind);
		Assert.Null(live.Change.Message);
		Assert.Null(live.Change.RecipientState);

		MessageDeviceState state = Assert.IsType<MessageDeviceState>(live.Change.DeviceState);
		Assert.Equal(accepted.Message.MessageId, state.MessageId);
		Assert.Equal(group.Second.Instance.Id, state.Device.LocalInstanceId);
		Assert.Equal(group.Second.Device.Id, state.Device.DeviceRegistrationId);
		Assert.NotNull(state.CiphertextStoredAtUtc);
		Assert.Null(state.ReceivedAtUtc);
	}

	[Fact]
	public async Task StatusDelivery_StillKnowsItsKind_AfterTheChatJournalWasPruned()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);
		Assert.True((await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal, Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received))).IsSuccess);

		SyncOutboxEntity row = Assert.Single(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.RecipientState));

		// Retention prunes the journal after 30 days; a task that waited longer than that must still know
		// what it delivers. Nothing below reads SyncChatChanges.
		string chatId = accepted.Message.ChatId;
		await db.SyncChatChanges.Where(c => c.ChatId == chatId).ExecuteDeleteAsync();

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		DeliveryOutcome outcome = await context.Delivery(consumer).DeliverAsync(TaskFor(row));

		Assert.False(outcome.IsUnprocessable);
		Assert.True(outcome.DeviceSends > 0);
		Assert.All(context.Hub.Sent, s => Assert.Equal(ChangeKind.RecipientState, EventOf(s).Change.Kind));

		// And the message body is NOT re-shipped: a status task never materializes a content key.
		Assert.All(context.Hub.Sent, s => Assert.Null(EventOf(s).Change.Message));
	}

	[Fact]
	public async Task PrivateStatusDeliveryUnderAFrozenStint_SendsNothing_AndLeavesTheArchiveBoundAlone()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		SyncMembershipEntity frozen = await StintAsync(
			db, accepted.Message.ChatId, group.Third.Account.Id, SyncMembershipState.Frozen);

		// The archive's bound: stamped at freeze time and fixed forever. Everything this test does happens
		// after it.
		long bound = (await db.SyncMembershipMetadataSnapshots.AsNoTracking()
			.SingleAsync(s => s.MembershipId == frozen.Id)).CapturedAtChangeSequence;

		// The removed member's device confirms the archive it downloaded - the whole reason the private
		// record exists, and in bulk the common case: one receipt per archived message.
		Assert.True((await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Third.Principal,
			Receipt(group.Third, frozen, accepted.Message.MessageId, ReceiptKind.Received))).IsSuccess);

		SyncOutboxEntity row = Assert.Single(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.DeviceState));

		// The structural reason nothing may be emitted: the change sits ABOVE the frozen bound, so an
		// event carrying it would name a position the archive's own stream can never reach.
		Assert.True(row.ChangeSequence > bound);

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		DeliveryOutcome outcome = await context.Delivery(consumer).DeliverAsync(TaskFor(row));

		// Consumed as a no-op, not as a failure: the task is finished and never retried, and no device -
		// not even the reporting instance - is addressed.
		Assert.False(outcome.IsUnprocessable);
		Assert.Equal(0, outcome.DeviceSends);
		Assert.Empty(context.Hub.Sent);

		// The archive bound did not move, and the private state, its journal entry and the ACK are all
		// still durable - delivery writes nothing either way.
		Assert.Equal(
			bound,
			(await db.SyncMembershipMetadataSnapshots.AsNoTracking()
				.SingleAsync(s => s.MembershipId == frozen.Id)).CapturedAtChangeSequence);

		SyncMessageDeviceStateEntity? device = await DeviceStateAsync(db, accepted.Message.MessageId, group.Third);
		Assert.NotNull(device);
		Assert.NotNull(device.ReceivedAtUtc);

		string chatId = accepted.Message.ChatId;
		Assert.NotEmpty(await db.SyncChatChanges.AsNoTracking()
			.Where(c => c.ChatId == chatId
				&& c.ReferenceId == accepted.Message.MessageId
				&& c.Kind == SyncChangeKind.DeviceState)
			.ToListAsync());
	}

	// ---------------------------------------------------------------- one person, one public state

	[Fact]
	public async Task TwoStintRowsOfOnePerson_ProjectAsOneCanonicalState_AndTheFrozenCaptureAgrees()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		// The incoming message both of this person's stints will end up holding a public row for.
		Accepted incoming = await SendAsync(context, db, group.Founder, group.Chat.Id, clientOperationId: "op-incoming");
		string chatId = incoming.Message.ChatId;

		// Their own send marks it logically Seen with reason OwnSend - under the stint they are in NOW.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		await SendAsync(context, db, group.Second, group.Chat.Id, clientOperationId: "op-own-1", text: "mine");

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId)).IsSuccess);

		SyncMembershipEntity archived = await StintAsync(db, chatId, group.Second.Account.Id, SyncMembershipState.Frozen);

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Second.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.LastTenDays, 0))).IsSuccess);

		SyncMembershipEntity rejoined = await StintAsync(db, chatId, group.Second.Account.Id, SyncMembershipState.Active);

		// A send under the NEW stint. OwnSend asks "is this already Seen for THIS stint", which the old
		// stint's row does not answer, so it writes a SECOND row for the same (message, person) - with a
		// later time. This is the shape the public readers have to cope with; no receipt is involved.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		await SendAsync(context, db, group.Second, group.Chat.Id, clientOperationId: "op-own-2", text: "mine again");

		List<SyncMessageRecipientStateEntity> rows = await RecipientStatesAsync(db, incoming.Message.MessageId, group.Second);
		Assert.Equal(2, rows.Count);

		SyncMessageRecipientStateEntity archiveRow = Assert.Single(rows, r => r.MembershipId == archived.Id);
		SyncMessageRecipientStateEntity currentRow = Assert.Single(rows, r => r.MembershipId == rejoined.Id);
		Assert.True(archiveRow.SeenAtUtc < currentRow.SeenAtUtc);

		long head = (await db.SyncChatSequenceCounters.AsNoTracking().SingleAsync(c => c.ChatId == chatId)).NextChangeSequence - 1;
		SyncScopeContext scope = new(chatId, rejoined.Id, rejoined.RightsVersion, head, false, rejoined.Id);

		MessageRecipientState? state = await context.Projection(db).RecipientStateAsync(
			scope, incoming.Message.MessageId, group.Second.Account.Id, default);

		// ONE state, and it is the EARLIEST proof of each fact - not whichever row the query happened to
		// return first. A person's public status must not jump forward because a newer stint repeated an
		// older mark.
		Assert.NotNull(state);
		Assert.Equal(Services.Sessions.RfcTimestamp.Format(archiveRow.ReceivedAtUtc!.Value), state.ReceivedAtUtc);
		Assert.NotNull(state.Seen);
		Assert.Equal(SeenReason.OwnSend, state.Seen.Reason);
		Assert.Equal(Services.Sessions.RfcTimestamp.Format(archiveRow.SeenAtUtc!.Value), state.Seen.SeenAtUtc);

		// The sum of the contributing rows: monotone, because every row only increments and none is ever
		// deleted - a maximum would have swallowed the second row's change.
		Assert.Equal((archiveRow.Version + currentRow.Version).ToString(), state.Version);

		// And a PAGE describes the person once, not once per stint row.
		SyncCallerIdentity caller = (await SyncCallerIdentity.ResolveAsync(db, group.Founder.Principal, default))!;
		SyncMembershipEntity authorStint = await StintAsync(db, chatId, group.Founder.Account.Id, SyncMembershipState.Active);
		SyncMessageEntity messageRow = await db.SyncMessages.AsNoTracking().SingleAsync(m => m.Id == incoming.Message.MessageId);

		IReadOnlyList<SyncProjection.MessageGroup> described = await context.Projection(db).DescribeMessagesAsync(
			new SyncScopeContext(chatId, authorStint.Id, authorStint.RightsVersion, head, false, authorStint.Id),
			[messageRow], caller, includeStatuses: true, default);

		Assert.Single(
			Assert.Single(described).Changes,
			c => string.Equals(c.RecipientState?.RecipientAccountContextId, group.Second.Account.Id, StringComparison.Ordinal));

		// Freezing the SECOND stint captures the same fold - the archive records the state the chat was
		// actually showing, so an immutable snapshot cannot be born holding the later time.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId)).IsSuccess);

		SyncMembershipMessageStatusSnapshotEntity snapshot = await db.SyncMembershipMessageStatusSnapshots.AsNoTracking()
			.SingleAsync(s => s.MembershipId == rejoined.Id
				&& s.MessageId == incoming.Message.MessageId
				&& s.RecipientAccountId == group.Second.Account.Id);

		Assert.Equal(archiveRow.ReceivedAtUtc, snapshot.ReceivedAtUtc);
		Assert.Equal(archiveRow.SeenAtUtc, snapshot.SeenAtUtc);
		Assert.Equal(SyncSeenReason.OwnSend, snapshot.SeenReason);

		// The FIRST stint's snapshot is untouched by everything that happened after it was taken.
		SyncMembershipMessageStatusSnapshotEntity first = await db.SyncMembershipMessageStatusSnapshots.AsNoTracking()
			.SingleAsync(s => s.MembershipId == archived.Id
				&& s.MessageId == incoming.Message.MessageId
				&& s.RecipientAccountId == group.Second.Account.Id);

		Assert.Equal(archiveRow.SeenAtUtc, first.SeenAtUtc);
	}

	[Fact]
	public void FoldingOnePersonsRows_KeepsTheEarliestFacts_AndMovesForwardOnALateChange()
	{
		DateTime first = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
		DateTime later = first.AddMinutes(5);

		// An archive row that already carries the person's first proof, and a current-stint row written
		// after a rejoin.
		PublicRecipientState archive = new(1, first, first, SyncSeenReason.OwnSend);
		PublicRecipientState current = new(1, later, later, SyncSeenReason.Observed);

		PublicRecipientState folded = PublicRecipientState.Fold([archive, current]);

		Assert.Equal(first, folded.ReceivedAtUtc);
		Assert.Equal(first, folded.SeenAtUtc);
		// The first accepted transition owns the REASON too, not only the time.
		Assert.Equal(SyncSeenReason.OwnSend, folded.SeenReason);
		Assert.Equal(2, folded.Version);

		// Row order is not an input: the same set folds to the same state.
		Assert.Equal(folded, PublicRecipientState.Fold([current, archive]));

		// A late change on EITHER row moves the aggregate version forward and moves no recorded time. A
		// maximum would have reported 2 for the first case (unchanged, i.e. a lost change); an arbitrary
		// row's own version could report 1 (a regression).
		PublicRecipientState afterCurrentMoved = PublicRecipientState.Fold([archive, current with { Version = 2 }]);
		Assert.Equal(3, afterCurrentMoved.Version);
		Assert.Equal(first, afterCurrentMoved.SeenAtUtc);
		Assert.Equal(SyncSeenReason.OwnSend, afterCurrentMoved.SeenReason);

		// A third row appearing (another rejoin) starts at 0, so it cannot lower the aggregate either.
		Assert.Equal(
			2,
			PublicRecipientState.Fold([archive, current, new PublicRecipientState(0, null, null, null)]).Version);

		// Nothing known at all is the explicit empty state, which is what the single-row readers answered
		// before and what a frozen capture of a person who reported nothing must still record.
		PublicRecipientState nothing = PublicRecipientState.Fold([]);
		Assert.Equal(0, nothing.Version);
		Assert.Null(nothing.ReceivedAtUtc);
		Assert.Null(nothing.SeenAtUtc);
		Assert.Null(nothing.SeenReason);
	}

	[Fact]
	public async Task ReceiptWithAnUppercaseMessageId_IsMalformed_NotTerminallyNotApplicable()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);

		// A v1 message id is a CANONICAL lowercase UUID. Guid parsing is case-insensitive, so an uppercase
		// one has the right shape and names no row - it must be answered as the malformed request it is,
		// never as the terminal NotApplicable that would make a client casing bug drop every receipt it
		// ever sends.
		SyncResult<ReceiptAcceptance> result = await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal,
			Receipt(group.Second, stint, accepted.Message.MessageId.ToUpperInvariant(), ReceiptKind.Received));

		Assert.False(result.IsSuccess);
		Assert.Equal(SyncErrorCode.InvalidRequest, result.Failure!.Code);
		Assert.Null(await DeviceStateAsync(db, accepted.Message.MessageId, group.Second));
		Assert.Empty(await RecipientStatesAsync(db, accepted.Message.MessageId, group.Second));

		// The same id in its canonical form is accepted, so the refusal above is the casing and nothing
		// else.
		Assert.True((await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal,
			Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received, clientOperationId: "rcpt-lower")))
			.IsSuccess);
	}

	[Fact]
	public async Task DeliveryTaskThatDoesNotDescribeItsOwnOutboxRow_IsUnprocessable()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		SyncMembershipEntity stint = await StintAsync(
			db, accepted.Message.ChatId, group.Second.Account.Id, SyncMembershipState.Active);
		Assert.True((await context.Statuses(db).AcceptSyncReceiptAsync(
			group.Second.Principal, Receipt(group.Second, stint, accepted.Message.MessageId, ReceiptKind.Received))).IsSuccess);

		SyncOutboxEntity row = Assert.Single(await OutboxAsync(db, accepted.Message.MessageId, SyncChangeKind.RecipientState));

		// The row - not the broker payload - is the authority for what a task delivers, so a task that
		// disagrees with its own row is recorded with its reason instead of being delivered.
		DeliveryTask mismatched = TaskFor(row) with { ChangeSequence = (row.ChangeSequence + 1).ToString() };

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		Assert.True((await context.Delivery(consumer).DeliverAsync(mismatched)).IsUnprocessable);
		Assert.Empty(context.Hub.Sent);
	}
}
