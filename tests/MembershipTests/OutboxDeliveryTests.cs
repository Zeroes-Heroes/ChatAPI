using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Services.Authorship.Models;
using Services.Crypto;
using Services.Delivery;
using Services.Delivery.Interface;
using Services.Delivery.Models;
using Services.Delivery.Service;
using Services.Messaging.Models;
using Services.Sync.Contracts;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// plan/12-outbox-delivery.md against a real PostgreSQL and the real services: the outbox is claimed
/// with a bounded lease, success is recorded only after the publish actually succeeded, the live event
/// carries the whole encrypted message with the key grant and the author's verification key, and only
/// the accounts whose historical rights permit the message - through devices that are trusted right now
/// - receive it. The broker itself is the one seam a test process cannot have; everything above it runs
/// for real.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class OutboxDeliveryTests(DatabaseFixture fixture)
{
	private static readonly JsonSerializerOptions SyncJson = SyncProtocol.CreateJsonOptions();

	/// <summary>One accepted v1 message plus everything a test needs to check what was delivered about it.</summary>
	private sealed record Accepted(
		AcceptedMessage Message, long OutboxId, MessageBinding Binding, byte[] Spki, string Plaintext);

	/// <summary>Accepts a real signed package from <paramref name="sender"/>, exactly as the hub's v1 send does.</summary>
	private static async Task<Accepted> SendAsync(
		MembershipTestContext context, AppDbContext db, TestMember sender, int legacyChatId,
		string clientOperationId = "op-1", string text = "hello")
	{
		using TestAuthorKey key = new();
		AuthorKeyReference reference = new(
			sender.Account.Id, legacyChatId.ToString(), $"authkey-{Guid.NewGuid():N}", "1");

		Result<AuthorKeyRegistration> registered = await context.AuthorKeys(db).RegisterAsync(sender.Principal, reference, key.Spki);
		Assert.True(registered.IsSuccess);

		string chatId = legacyChatId.ToString();
		SyncMembershipEntity stint = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chatId && m.AccountId == sender.Account.Id && m.State == SyncMembershipState.Active);

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

		return new Accepted(accepted.Result, result.Data.OutboxId!.Value, binding, key.Spki, text);
	}

	private static DeliveryTask TaskFor(Accepted accepted) => new(
		SyncProtocol.Version,
		accepted.OutboxId,
		accepted.Message.ChatId,
		accepted.Message.MessageId,
		accepted.Message.ChangeSequence,
		DeliveryEventId.ForChatChange(accepted.Message.ChatId, long.Parse(accepted.Message.ChangeSequence)));

	private static LiveEvent EventOf(SentEvent sent)
	{
		Assert.Equal("sync-change", sent.Event);
		return JsonSerializer.Deserialize<LiveEvent>(Assert.IsType<string>(sent.Payload), SyncJson)!;
	}

	private static async Task<SyncOutboxEntity> RowAsync(MembershipTestContext context, long id)
	{
		await using AppDbContext read = context.CreateContext();
		return await read.SyncOutbox.AsNoTracking().SingleAsync(o => o.Id == id);
	}

	/// <summary>
	/// Finishes every row an earlier test (or an earlier run against this same durable database) left
	/// behind, so a claim below is about THIS test's own row rather than about the table's history.
	/// Called before the message under test is accepted, never after.
	/// </summary>
	private static async Task QuiesceOutboxAsync(MembershipTestContext context)
	{
		await using AppDbContext db = context.CreateContext();
		await db.Database.ExecuteSqlRawAsync("""
			UPDATE "SyncOutbox"
			SET "Status" = 2, "LeaseOwner" = NULL, "LeasedUntilUtc" = NULL, "NextAttemptAtUtc" = NULL
			WHERE "Status" <> 2
			""");
	}

	// ---------------------------------------------------------------- the outbox queue

	[Fact]
	public async Task Claim_TakesThePendingRowUnderALease_AndNobodyElseCanTakeItUntilItExpires()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await QuiesceOutboxAsync(context);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		await using AppDbContext first = context.CreateContext();
		IReadOnlyList<OutboxClaim> claimed = await context.Outbox(first).ClaimAsync("replica-a", 10);
		OutboxClaim claim = Assert.Single(claimed, c => c.Id == accepted.OutboxId);
		Assert.Equal(accepted.Message.MessageId, claim.MessageId);
		Assert.Equal(long.Parse(accepted.Message.ChangeSequence), claim.ChangeSequence);
		Assert.Equal(1, claim.AttemptCount);

		SyncOutboxEntity row = await RowAsync(context, accepted.OutboxId);
		Assert.Equal(SyncOutboxStatus.Leased, row.Status);
		Assert.Equal("replica-a", row.LeaseOwner);
		Assert.NotNull(row.LeasedUntilUtc);

		// A second replica polling while the lease is live takes nothing - no double publish, and no
		// waiting on the other replica either.
		await using AppDbContext second = context.CreateContext();
		Assert.DoesNotContain(
			await context.Outbox(second).ClaimAsync("replica-b", 10),
			c => c.Id == accepted.OutboxId);
	}

	[Fact]
	public async Task Claim_RetakesTheRow_OnceTheLeaseOfACrashedPublisherExpired()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await QuiesceOutboxAsync(context);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		await using AppDbContext first = context.CreateContext();
		Assert.Contains(await context.Outbox(first).ClaimAsync("replica-a", 10), c => c.Id == accepted.OutboxId);

		// The publisher died between claiming and publishing; nothing marked it published.
		context.Clock.Advance(TimeSpan.FromSeconds(61));

		await using AppDbContext second = context.CreateContext();
		OutboxClaim retaken = Assert.Single(
			await context.Outbox(second).ClaimAsync("replica-b", 10), c => c.Id == accepted.OutboxId);
		Assert.Equal(2, retaken.AttemptCount);
		Assert.Equal("replica-b", (await RowAsync(context, accepted.OutboxId)).LeaseOwner);
	}

	[Fact]
	public async Task MarkPublished_EndsTheRow_AndNoLaterPollTakesItAgain()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await QuiesceOutboxAsync(context);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		await using AppDbContext publisher = context.CreateContext();
		IOutboxStore outbox = context.Outbox(publisher);
		Assert.Contains(await outbox.ClaimAsync("replica-a", 10), c => c.Id == accepted.OutboxId);
		await outbox.MarkPublishedAsync(accepted.OutboxId);

		SyncOutboxEntity row = await RowAsync(context, accepted.OutboxId);
		Assert.Equal(SyncOutboxStatus.Published, row.Status);
		Assert.NotNull(row.PublishedAtUtc);
		Assert.Null(row.LeaseOwner);

		context.Clock.Advance(TimeSpan.FromHours(1));
		await using AppDbContext later = context.CreateContext();
		Assert.DoesNotContain(await context.Outbox(later).ClaimAsync("replica-b", 10), c => c.Id == accepted.OutboxId);
	}

	[Fact]
	public async Task ReleaseForRetry_SchedulesAGrowingBackoff_AndStopsRetryingOnceAttemptsAreExhausted()
	{
		// A whole-second clock: the next attempt is computed in PostgreSQL, whose timestamps are
		// microsecond-precise, so an assertion on the exact instant needs a start that survives the round
		// trip unchanged.
		MembershipTestContext context = new(fixture, new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero));
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await QuiesceOutboxAsync(context);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		await using AppDbContext worker = context.CreateContext();
		IOutboxStore outbox = context.Outbox(worker);

		Assert.Contains(await outbox.ClaimAsync("replica-a", 10), c => c.Id == accepted.OutboxId);
		await outbox.ReleaseForRetryAsync(accepted.OutboxId, "broker unreachable");

		SyncOutboxEntity afterFirst = await RowAsync(context, accepted.OutboxId);
		Assert.Equal(SyncOutboxStatus.Pending, afterFirst.Status);
		Assert.Equal("broker unreachable", afterFirst.LastError);
		Assert.Null(afterFirst.LeaseOwner);
		// Attempt 1 -> base delay.
		Assert.Equal(context.Clock.GetUtcNow().UtcDateTime.AddSeconds(2), afterFirst.NextAttemptAtUtc);

		// A waiting row is not due yet, however often the publisher polls.
		Assert.DoesNotContain(await outbox.ClaimAsync("replica-a", 10), c => c.Id == accepted.OutboxId);

		context.Clock.Advance(TimeSpan.FromSeconds(2));
		Assert.Contains(await outbox.ClaimAsync("replica-a", 10), c => c.Id == accepted.OutboxId);
		await outbox.ReleaseForRetryAsync(accepted.OutboxId, "broker unreachable");

		// Attempt 2 -> doubled.
		Assert.Equal(
			context.Clock.GetUtcNow().UtcDateTime.AddSeconds(4),
			(await RowAsync(context, accepted.OutboxId)).NextAttemptAtUtc);

		context.Clock.Advance(TimeSpan.FromSeconds(4));
		Assert.Contains(await outbox.ClaimAsync("replica-a", 10), c => c.Id == accepted.OutboxId);
		await outbox.ReleaseForRetryAsync(accepted.OutboxId, "broker unreachable");

		// MaxAttempts (3) reached: kept for diagnostics with its reason, never automatically claimed again.
		SyncOutboxEntity exhausted = await RowAsync(context, accepted.OutboxId);
		Assert.Equal(SyncOutboxStatus.Failed, exhausted.Status);
		Assert.Null(exhausted.NextAttemptAtUtc);
		Assert.Equal("broker unreachable", exhausted.LastError);

		context.Clock.Advance(TimeSpan.FromDays(1));
		Assert.DoesNotContain(await outbox.ClaimAsync("replica-a", 10), c => c.Id == accepted.OutboxId);
	}

	[Fact]
	public async Task MarkUnprocessable_KeepsTheRowForDiagnostics_AndNeverRetriesIt()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await QuiesceOutboxAsync(context);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		await using AppDbContext worker = context.CreateContext();
		IOutboxStore outbox = context.Outbox(worker);
		await outbox.MarkUnprocessableAsync(accepted.OutboxId, "no protected key row");

		SyncOutboxEntity row = await RowAsync(context, accepted.OutboxId);
		Assert.Equal(SyncOutboxStatus.Failed, row.Status);
		Assert.Null(row.NextAttemptAtUtc);
		Assert.Equal("no protected key row", row.LastError);

		context.Clock.Advance(TimeSpan.FromDays(7));
		Assert.DoesNotContain(await outbox.ClaimAsync("replica-a", 10), c => c.Id == accepted.OutboxId);
	}

	// ---------------------------------------------------------------- live delivery

	[Fact]
	public async Task Delivery_CarriesTheWholeEncryptedMessage_ItsKeyGrantAndTheAuthorsVerificationKey()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id, text: "live delivery");

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		DeliveryOutcome outcome = await context.Delivery(consumer).DeliverAsync(TaskFor(accepted));

		// Three people, one trusted device each - including the author's own.
		Assert.False(outcome.IsUnprocessable);
		Assert.Equal(3, outcome.DeviceSends);
		Assert.Equal(3, context.Hub.Sent.Count);

		SentEvent toSecond = Assert.Single(context.Hub.Sent,
			s => s.Audience.Single() == $"sync-device:{group.Second.Device.Id}");
		LiveEvent live = EventOf(toSecond);

		Assert.Equal(SyncProtocol.Version, live.ProtocolVersion);
		Assert.Equal(group.Second.Account.Id, live.AccountContextId);
		Assert.Equal(ChangeKind.Message, live.Change.Kind);
		Assert.Equal(accepted.Message.ChangeSequence, live.Change.Position.Sequence);
		Assert.Equal(StreamKind.Chat, live.Change.Position.StreamKind);
		Assert.Equal(accepted.Message.ChatId, live.Change.Position.ChatId);

		MessageDelivery delivery = Assert.IsType<MessageDelivery>(live.Change.Message);

		// The whole encrypted message, byte for byte what the author signed.
		Assert.Equal(accepted.Message.MessageId, delivery.Message.MessageId);
		Assert.Equal(group.Founder.Account.Id, delivery.Message.SenderAccountContextId);
		Assert.Equal(accepted.Message.MessageSequence, delivery.Message.MessageSequence);
		SyncMessageEntity stored = await consumer.SyncMessages.AsNoTracking().SingleAsync(m => m.Id == accepted.Message.MessageId);
		Assert.Equal(stored.SignedPackage, delivery.Message.SignedPackage);

		// The recipient's own scope, and the key grant names this very message.
		Assert.Equal(accepted.Message.ChatId, delivery.Scope.ChatId);
		Assert.Equal(live.Change.Position.MembershipId, delivery.Scope.MembershipId);
		Assert.Equal(live.RightsVersion, delivery.Scope.RightsVersion);
		Assert.Equal(accepted.Message.MessageId, delivery.Key.MessageId);

		// The author key is the registered one, under the complete reference the package is bound to, and
		// it really verifies the delivered signed bytes.
		Assert.Equal(accepted.Binding.AuthorKey, delivery.AuthorVerificationKey.Reference);
		Assert.True(Base64Url.TryDecode(delivery.AuthorVerificationKey.Spki, out byte[] spki));
		Assert.Equal(accepted.Spki, spki);
		string[] segments = delivery.Message.SignedPackage.Split('.');
		using (ECDsa verifier = ECDsa.Create())
		{
			verifier.ImportSubjectPublicKeyInfo(spki, out _);
			Assert.True(Base64Url.TryDecode(segments[2], out byte[] signature));
			Assert.True(verifier.VerifyData(
				Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"), signature,
				HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
		}

		// And the granted key really opens the delivered ciphertext - the recipient can read this message
		// from the live event alone.
		Assert.True(Base64Url.TryDecode(segments[1], out byte[] payloadBytes));
		SignedMessagePayload payload = JsonSerializer.Deserialize<SignedMessagePayload>(payloadBytes, SyncJson)!;
		Assert.True(Base64Url.TryDecode(delivery.Key.MessageKey, out byte[] messageKey));
		Assert.True(Base64Url.TryDecode(payload.Content.Nonce, out byte[] nonce));
		Assert.True(Base64Url.TryDecode(payload.Content.Ciphertext, out byte[] ciphertext));
		Assert.True(Base64Url.TryDecode(payload.Content.Tag, out byte[] tag));
		byte[] plaintext = new byte[ciphertext.Length];
		using (AesGcm aes = new(messageKey, 16))
		{
			aes.Decrypt(nonce, ciphertext, tag, plaintext, TestAuthorKey.Aad(payload.Binding));
		}

		Assert.Equal("live delivery", Encoding.UTF8.GetString(plaintext));
	}

	[Fact]
	public async Task Delivery_ReachesEveryOtherDeviceOfTheAuthorToo()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		TestMember authorsSecondDevice = await context.AddDeviceAsync(db, group.Founder);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		await context.Delivery(consumer).DeliverAsync(TaskFor(accepted));

		SentEvent toOtherDevice = Assert.Single(context.Hub.Sent,
			s => s.Audience.Single() == $"sync-device:{authorsSecondDevice.Device.Id}");
		LiveEvent live = EventOf(toOtherDevice);

		// "Всички устройства на акаунта получават собствените съобщения с пълно съдържание" - including
		// the key, so the other device can read it rather than see an undecryptable record.
		Assert.Equal(group.Founder.Account.Id, live.AccountContextId);
		Assert.Equal(accepted.Message.MessageId, live.Change.Message!.Message.MessageId);
		Assert.False(string.IsNullOrEmpty(live.Change.Message!.Key.MessageKey));
	}

	[Fact]
	public async Task Delivery_SkipsAMemberRemovedBeforeTheMessage()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		Assert.True((await context.Membership(db).RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);
		context.Clock.Advance(TimeSpan.FromMinutes(1));

		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		DeliveryOutcome outcome = await context.Delivery(consumer).DeliverAsync(TaskFor(accepted));

		// The archive is read-only and ends at the removal: a message created afterwards is not part of it.
		Assert.Equal(2, outcome.DeviceSends);
		Assert.DoesNotContain(context.Hub.Sent, s => s.Audience.Contains($"sync-device:{group.Third.Device.Id}"));
		Assert.Contains(context.Hub.Sent, s => s.Audience.Contains($"sync-device:{group.Second.Device.Id}"));
	}

	[Fact]
	public async Task Delivery_SkipsARevokedDevice_ButStillReachesThatPersonsTrustedOne()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		TestMember secondDeviceOfSecond = await context.AddDeviceAsync(db, group.Second);

		SyncDeviceRegistrationEntity revoked = await db.SyncDeviceRegistrations.SingleAsync(d => d.Id == group.Second.Device.Id);
		revoked.TrustState = DeviceTrustState.Revoked;
		revoked.RevokedAtUtc = context.Clock.GetUtcNow().UtcDateTime;
		revoked.TrustVersion += 1;
		await db.SaveChangesAsync();

		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		await context.Delivery(consumer).DeliverAsync(TaskFor(accepted));

		// Current trust is re-read at delivery time, so a device revoked while the task waited is never
		// handed a key grant - while the same person's trusted device still receives the message.
		Assert.DoesNotContain(context.Hub.Sent, s => s.Audience.Contains($"sync-device:{group.Second.Device.Id}"));
		Assert.Contains(context.Hub.Sent, s => s.Audience.Contains($"sync-device:{secondDeviceOfSecond.Device.Id}"));
	}

	[Fact]
	public async Task Delivery_SkipsASupersededAccountContext()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		// That phone started over (plan/06): the old context keeps its rows, and can never read this chat
		// again - so it is not a recipient and its devices are not addressed.
		SyncAccountEntity ended = await db.SyncAccounts.SingleAsync(a => a.Id == group.Third.Account.Id);
		ended.EndedAtUtc = context.Clock.GetUtcNow().UtcDateTime;
		await db.SaveChangesAsync();

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		DeliveryOutcome outcome = await context.Delivery(consumer).DeliverAsync(TaskFor(accepted));

		Assert.Equal(2, outcome.DeviceSends);
		Assert.DoesNotContain(context.Hub.Sent, s => s.Audience.Contains($"sync-device:{group.Third.Device.Id}"));
	}

	[Fact]
	public async Task Delivery_RepeatedForTheSameTask_RepeatsTheSameEventId()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		await context.Delivery(consumer).DeliverAsync(TaskFor(accepted));
		await context.Delivery(consumer).DeliverAsync(TaskFor(accepted));

		// A republished or redelivered task is safe precisely because the event id is derived from the
		// chat change, not minted per attempt ("Устойчивите event IDs позволяват безопасни повторения").
		string[] eventIds = [.. context.Hub.Sent.Select(s => EventOf(s).Change.EventId).Distinct()];
		Assert.Equal($"{accepted.Message.ChatId}:{accepted.Message.ChangeSequence}", Assert.Single(eventIds));
		Assert.Equal(6, context.Hub.Sent.Count);
	}

	[Fact]
	public async Task Delivery_WithoutItsProtectedKey_IsUnprocessableAndSendsNothing()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		db.SyncMessageKeys.Remove(await db.SyncMessageKeys.SingleAsync(k => k.MessageId == accepted.Message.MessageId));
		await db.SaveChangesAsync();

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		DeliveryOutcome outcome = await context.Delivery(consumer).DeliverAsync(TaskFor(accepted));

		Assert.True(outcome.IsUnprocessable);
		Assert.Empty(context.Hub.Sent);
	}

	[Fact]
	public async Task Delivery_OfAnUnknownMessage_IsUnprocessable()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		DeliveryTask ghost = TaskFor(accepted) with { MessageId = Guid.NewGuid().ToString("D") };

		context.Hub.Sent.Clear();
		await using AppDbContext consumer = context.CreateContext();
		Assert.True((await context.Delivery(consumer).DeliverAsync(ghost)).IsUnprocessable);
		Assert.Empty(context.Hub.Sent);
	}

	// ---------------------------------------------------------------- the two workers

	/// <summary>The one seam a test process cannot have. Records what was published, and can refuse like an unreachable broker.</summary>
	private sealed class TestBroker : IDeliveryBroker
	{
		public List<DeliveryTask> Published { get; } = [];

		public bool Refuse { get; set; }

		public Task PublishAsync(DeliveryTask task, CancellationToken ct = default)
		{
			if (Refuse)
			{
				// What an unconfirmed/unroutable publish looks like to the caller: an exception, never a
				// silent success.
				throw new InvalidOperationException("broker unreachable");
			}

			Published.Add(task);
			return Task.CompletedTask;
		}

		public Task RunConsumerAsync(Func<DeliveryTask, CancellationToken, Task<bool>> handler, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	/// <summary>An outbox whose writes always fail, for the one case where nothing durable can be recorded.</summary>
	private sealed class BrokenOutboxStore : IOutboxStore
	{
		public Task<IReadOnlyList<OutboxClaim>> ClaimAsync(string owner, int max, CancellationToken ct = default) =>
			throw new InvalidOperationException("database unavailable");

		public Task MarkPublishedAsync(long id, CancellationToken ct = default) =>
			throw new InvalidOperationException("database unavailable");

		public Task ReleaseForRetryAsync(long id, string reason, CancellationToken ct = default) =>
			throw new InvalidOperationException("database unavailable");

		public Task MarkUnprocessableAsync(long id, string reason, CancellationToken ct = default) =>
			throw new InvalidOperationException("database unavailable");
	}

	/// <summary>A scope factory over the real services, exactly as the workers resolve them in production.</summary>
	private static ServiceProvider WorkerServices(MembershipTestContext context, bool brokenOutbox = false)
	{
		ServiceCollection services = new();
		services.AddScoped(_ => context.CreateContext());
		services.AddScoped<ILiveDeliveryService>(provider => context.Delivery(provider.GetRequiredService<AppDbContext>()));

		if (brokenOutbox)
		{
			services.AddScoped<IOutboxStore, BrokenOutboxStore>();
		}
		else
		{
			services.AddScoped<IOutboxStore>(provider => context.Outbox(provider.GetRequiredService<AppDbContext>()));
		}

		return services.BuildServiceProvider();
	}

	private static OutboxPublisherHostedService Publisher(ServiceProvider provider, IDeliveryBroker broker) =>
		new(provider.GetRequiredService<IServiceScopeFactory>(), broker, new OutboxPublishSignal(),
			Options.Create(MembershipTestContext.DeliveryConfiguration()),
			NullLogger<OutboxPublisherHostedService>.Instance);

	private static DeliveryConsumerHostedService Consumer(ServiceProvider provider, IDeliveryBroker broker) =>
		new(provider.GetRequiredService<IServiceScopeFactory>(), broker,
			Options.Create(MembershipTestContext.DeliveryConfiguration()),
			NullLogger<DeliveryConsumerHostedService>.Instance);

	[Fact]
	public async Task Publisher_MarksARowPublished_OnlyAfterTheBrokerAcceptedIt()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await QuiesceOutboxAsync(context);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		await using ServiceProvider provider = WorkerServices(context);
		TestBroker broker = new();

		Assert.NotEqual(
			OutboxPublisherHostedService.PortionOutcome.Idle,
			await Publisher(provider, broker).PublishOnePortionAsync(CancellationToken.None));

		DeliveryTask published = Assert.Single(broker.Published, t => t.OutboxId == accepted.OutboxId);
		Assert.Equal(accepted.Message.MessageId, published.MessageId);
		Assert.Equal($"{accepted.Message.ChatId}:{accepted.Message.ChangeSequence}", published.EventId);
		Assert.Equal(SyncOutboxStatus.Published, (await RowAsync(context, accepted.OutboxId)).Status);
	}

	[Fact]
	public async Task Publisher_HandsRowsBackWithTheirBackoff_WhenTheBrokerRefuses()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await QuiesceOutboxAsync(context);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		await using ServiceProvider provider = WorkerServices(context);
		TestBroker broker = new() { Refuse = true };

		Assert.Equal(
			OutboxPublisherHostedService.PortionOutcome.BrokerUnavailable,
			await Publisher(provider, broker).PublishOnePortionAsync(CancellationToken.None));

		// Nothing was marked published that the broker never confirmed, and the row is waiting rather than
		// stuck under a dead lease.
		SyncOutboxEntity row = await RowAsync(context, accepted.OutboxId);
		Assert.Equal(SyncOutboxStatus.Pending, row.Status);
		Assert.Null(row.LeaseOwner);
		Assert.NotNull(row.NextAttemptAtUtc);
		Assert.Contains("broker unreachable", row.LastError ?? string.Empty);
	}

	[Fact]
	public async Task Consumer_AcknowledgesAfterDelivering()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		context.Hub.Sent.Clear();
		await using ServiceProvider provider = WorkerServices(context);
		Assert.True(await Consumer(provider, new TestBroker()).HandleAsync(TaskFor(accepted), CancellationToken.None));
		Assert.Equal(3, context.Hub.Sent.Count);
	}

	[Fact]
	public async Task Consumer_RecordsAnUndeliverableRecord_AndAcknowledges()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		db.SyncMessageKeys.Remove(await db.SyncMessageKeys.SingleAsync(k => k.MessageId == accepted.Message.MessageId));
		await db.SaveChangesAsync();

		context.Hub.Sent.Clear();
		await using ServiceProvider provider = WorkerServices(context);

		// Responsibility now rests durably in PostgreSQL, so the broker message is settled rather than
		// circulating forever.
		Assert.True(await Consumer(provider, new TestBroker()).HandleAsync(TaskFor(accepted), CancellationToken.None));

		SyncOutboxEntity row = await RowAsync(context, accepted.OutboxId);
		Assert.Equal(SyncOutboxStatus.Failed, row.Status);
		Assert.Null(row.NextAttemptAtUtc);
		Assert.NotNull(row.LastError);
		Assert.Empty(context.Hub.Sent);
	}

	[Fact]
	public async Task Consumer_RequeuesTheBrokerMessage_WhenEvenTheOutboxWriteFails()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		Accepted accepted = await SendAsync(context, db, group.Founder, group.Chat.Id);

		db.SyncMessageKeys.Remove(await db.SyncMessageKeys.SingleAsync(k => k.MessageId == accepted.Message.MessageId));
		await db.SaveChangesAsync();

		context.Hub.Sent.Clear();
		await using ServiceProvider provider = WorkerServices(context, brokenOutbox: true);

		// Nothing durable could be recorded, so the only place this task still exists is the broker: it
		// must NOT be acknowledged.
		Assert.False(await Consumer(provider, new TestBroker()).HandleAsync(TaskFor(accepted), CancellationToken.None));
	}
}
