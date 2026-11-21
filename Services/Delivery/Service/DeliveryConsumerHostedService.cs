using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Services.Delivery.Interface;
using Services.Delivery.Models;

namespace Services.Delivery.Service;

/// <summary>
/// The competing consumer every replica runs (plan/12-outbox-delivery.md: "Обработчиците потвърждават
/// след изпълнение или устойчиво прехвърляне на отговорността").
///
/// Exactly three outcomes per task, and the acknowledgement follows from which one happened:
/// <list type="number">
/// <item>delivered - acknowledged;</item>
/// <item>undeliverable forever (no message, key or author key) - the row is marked with its reason, so
/// responsibility now rests durably in PostgreSQL, and the task is acknowledged;</item>
/// <item>a transient failure - the row is handed back to the outbox with its backoff, which is a
/// durable transfer of responsibility, and only then acknowledged. If even THAT write fails, nothing
/// durable happened, so the broker message is returned to the queue instead.</item>
/// </list>
/// </summary>
public sealed class DeliveryConsumerHostedService(
	IServiceScopeFactory scopeFactory,
	IDeliveryBroker broker,
	IOptions<DeliveryOptions> options,
	ILogger<DeliveryConsumerHostedService> logger) : BackgroundService
{
	private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		DeliveryOptions config = options.Value;
		if (!config.IsConfigured)
		{
			logger.LogWarning("No delivery broker is configured (Delivery:ConnectionUri); the live delivery consumer is not running.");
			return;
		}

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				// Returns when the connection drops or the host stops; nothing in-flight is lost, because an
				// unacknowledged broker message is redelivered.
				await broker.RunConsumerAsync(HandleAsync, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Live delivery consumer stopped; reconnecting.");
			}

			try
			{
				await Task.Delay(ReconnectDelay, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
		}
	}

	/// <summary>
	/// Handles one task and answers whether its broker message may be acknowledged. Public so tests can
	/// exercise the three outcomes above against a real database without a broker.
	/// </summary>
	public async Task<bool> HandleAsync(DeliveryTask task, CancellationToken ct)
	{
		// One scope per task: a DbContext is never shared between concurrently dispatched deliveries.
		await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
		IServiceProvider services = scope.ServiceProvider;

		try
		{
			DeliveryOutcome outcome = await services.GetRequiredService<ILiveDeliveryService>().DeliverAsync(task, ct);

			if (outcome.IsUnprocessable)
			{
				logger.LogError(
					"Message {MessageId} of chat {ChatId} cannot be delivered: {Reason}. The outbox row is kept for diagnostics.",
					task.MessageId, task.ChatId, outcome.UnprocessableReason);
				await services.GetRequiredService<IOutboxStore>()
					.MarkUnprocessableAsync(task.OutboxId, outcome.UnprocessableReason!, CancellationToken.None);
			}

			return true;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Live delivery of message {MessageId} failed; returning it to the outbox.", task.MessageId);

			try
			{
				// CancellationToken.None deliberately: handing responsibility back is what makes acknowledging
				// this broker message safe, so it must not be skipped because the host is stopping.
				await services.GetRequiredService<IOutboxStore>()
					.ReleaseForRetryAsync(task.OutboxId, $"delivery failed: {ex.Message}", CancellationToken.None);
				return true;
			}
			catch (Exception releaseFailure)
			{
				// Nothing durable was recorded, so the only place this task still exists is the broker.
				logger.LogError(releaseFailure,
					"Could not return outbox row {OutboxId} for retry; the broker message is requeued instead.", task.OutboxId);
				return false;
			}
		}
	}
}
