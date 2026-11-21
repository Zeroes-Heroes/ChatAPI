using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Services.Delivery.Interface;
using Services.Delivery.Models;
using Services.Sync.Contracts;

namespace Services.Delivery.Service;

/// <summary>
/// Drains <c>SyncOutbox</c> into the broker (plan/12-outbox-delivery.md: "Устойчивият publisher взема
/// outbox задачи с ограничено владение и публикува през RabbitMQ.Client. Успехът се записва след broker
/// confirm и проверено маршрутизиране").
///
/// One bounded portion per scope, per MESSAGE_SYNC_ANALYSIS.md ("Фоновата работа създава и освобождава
/// scope за ограничена порция"): every tick creates its own scope/DbContext and disposes it, and no
/// DbContext is ever shared with the publish itself, which happens outside any transaction.
///
/// It owns nothing durable. A crash between claiming and publishing leaves the row leased, and the
/// lease's expiry is what lets any replica take it again; a crash between publishing and marking it
/// published costs a duplicate broker task with the same EventId, which the contract makes harmless.
/// </summary>
public sealed class OutboxPublisherHostedService(
	IServiceScopeFactory scopeFactory,
	IDeliveryBroker broker,
	IOutboxPublishSignal signal,
	IOptions<DeliveryOptions> options,
	ILogger<OutboxPublisherHostedService> logger) : BackgroundService
{
	/// <summary>How long to wait after a broker failure before touching it again, so an outage is not hammered once per claimed row.</summary>
	private static readonly TimeSpan BrokerBackoff = TimeSpan.FromSeconds(5);

	/// <summary>Identifies this replica's claims. Diagnostics only - correctness comes from the row lock and the lease, never from this string.</summary>
	private readonly string _owner = Owner();

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		DeliveryOptions config = options.Value;
		if (!config.IsConfigured)
		{
			// Said once, and then nothing happens: with no broker configured this deployment has no live
			// delivery at all. Accepted messages stay in the outbox and remain recoverable through sync.
			logger.LogWarning("No delivery broker is configured (Delivery:ConnectionUri); the outbox publisher is not running.");
			return;
		}

		TimeSpan poll = TimeSpan.FromSeconds(config.PollSeconds);

		while (!stoppingToken.IsCancellationRequested)
		{
			PortionOutcome outcome;
			try
			{
				outcome = await PublishOnePortionAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Outbox publisher portion failed; retrying after the poll interval.");
				outcome = PortionOutcome.Idle;
			}

			try
			{
				switch (outcome)
				{
					// The batch came back full: keep draining before sleeping, so a burst is not spread over
					// one poll interval per batch.
					case PortionOutcome.Full:
						continue;

					// The broker is the shared cause for everything in this batch; back off once rather than
					// re-claiming the same rows against the same outage.
					case PortionOutcome.BrokerUnavailable:
						await Task.Delay(BrokerBackoff, stoppingToken);
						continue;

					default:
						await signal.WaitAsync(poll, stoppingToken);
						continue;
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
		}
	}

	/// <summary>What one portion found, which is all the loop above needs to decide how to wait.</summary>
	public enum PortionOutcome
	{
		/// <summary>Nothing was due.</summary>
		Idle,

		/// <summary>Everything claimed was published, and the batch was not full.</summary>
		Drained,

		/// <summary>The batch came back full, so there is probably more work right now.</summary>
		Full,

		/// <summary>The broker refused; every unpublished row of the batch was handed back with its backoff.</summary>
		BrokerUnavailable,
	}

	/// <summary>
	/// Claims and publishes at most one batch. Public (not merely called from <see cref="ExecuteAsync"/>'s
	/// loop) so tests can exercise the claim/publish/mark sequence deterministically against a real
	/// database and a test broker, the same way SessionMaintenanceHostedService exposes one tick.
	/// </summary>
	public async Task<PortionOutcome> PublishOnePortionAsync(CancellationToken ct)
	{
		DeliveryOptions config = options.Value;

		await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
		IOutboxStore outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

		IReadOnlyList<OutboxClaim> claims = await outbox.ClaimAsync(_owner, config.ClaimBatchSize, ct);
		if (claims.Count == 0)
		{
			return PortionOutcome.Idle;
		}

		for (int i = 0; i < claims.Count; i++)
		{
			OutboxClaim claim = claims[i];
			DeliveryTask task = claim.ToTask(SyncProtocol.Version);
			try
			{
				// Returns only after the broker confirmed the publish AND the message routed; anything else
				// throws and the row goes back for another attempt.
				await broker.PublishAsync(task, ct);
				await outbox.MarkPublishedAsync(claim.Id, ct);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				// Shutting down: the lease expires on its own and another replica (or this one, after a
				// restart) takes the row. Nothing is marked published that was not confirmed.
				return PortionOutcome.Idle;
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex,
					"Publishing outbox row {OutboxId} (message {MessageId}) failed on attempt {Attempt}; it will be retried.",
					claim.Id, claim.MessageId, claim.AttemptCount);

				// The broker is the shared cause for every row still in this batch: all of them are handed
				// back with their own backoff rather than left leased until the lease expires.
				for (int unpublished = i; unpublished < claims.Count; unpublished++)
				{
					await outbox.ReleaseForRetryAsync(claims[unpublished].Id, $"publish failed: {ex.Message}", CancellationToken.None);
				}

				return PortionOutcome.BrokerUnavailable;
			}
		}

		return claims.Count >= config.ClaimBatchSize ? PortionOutcome.Full : PortionOutcome.Drained;
	}

	private static string Owner()
	{
		string owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
		// SyncOutbox.LeaseOwner is bounded at 100 characters; an unusually long machine name must not fail
		// the claim it only annotates.
		return owner.Length <= 100 ? owner : owner[^100..];
	}
}
