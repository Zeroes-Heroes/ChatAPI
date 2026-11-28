using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Services.Delivery.Interface;
using Services.Delivery.Models;

namespace Services.Delivery.Service;

/// <summary>
/// See <see cref="IDeliveryBroker"/>. The only RabbitMQ.Client usage in the codebase, kept to the two
/// operations the stage actually needs.
///
/// Publishing uses publisher confirmations WITH tracking and <c>mandatory</c>: the publish completes
/// only when the broker has both confirmed it and routed it to the live queue, and an unroutable or
/// nacked message throws instead of being silently accepted. That is what lets the caller mark an
/// outbox row published without hoping.
///
/// Both the exchange and the queue are declared durable, and every message is persistent, so a broker
/// restart does not drop queued work. Replicas are competing consumers on ONE shared queue, so a task
/// is handled once and the already-established server order is preserved per chat (one message per
/// chat change, in change order, with at-least-once redelivery deduplicated by EventId).
/// </summary>
public sealed class RabbitMqDeliveryBroker(
	IOptions<DeliveryOptions> options,
	ILogger<RabbitMqDeliveryBroker> logger) : IDeliveryBroker, IAsyncDisposable
{
	private static readonly JsonSerializerOptions TaskJson = new(JsonSerializerDefaults.Web);

	private readonly SemaphoreSlim _publishGate = new(1, 1);

	private IConnection? _publishConnection;
	private IChannel? _publishChannel;

	public async Task PublishAsync(DeliveryTask task, CancellationToken ct = default)
	{
		DeliveryOptions config = options.Value;
		byte[] body = JsonSerializer.SerializeToUtf8Bytes(task, TaskJson);

		// One channel per process for publishing, serialized: an IChannel is not safe for concurrent
		// publishes, and the publisher loop is single-threaded anyway - this gate only protects against a
		// reconnect racing a publish.
		await _publishGate.WaitAsync(ct);
		try
		{
			IChannel channel = await EnsurePublishChannelAsync(config, ct);

			BasicProperties properties = new()
			{
				Persistent = true,
				ContentType = "application/json",
				MessageId = task.EventId,
			};

			// mandatory: a message that routes nowhere (a missing/unbound queue) is returned and fails this
			// publish, rather than being confirmed into nothing.
			await channel.BasicPublishAsync(
				config.Exchange, config.LiveRoutingKey, mandatory: true, basicProperties: properties,
				body: body, cancellationToken: ct);
		}
		catch
		{
			// A failed publish may have left the channel/connection unusable; drop them so the next attempt
			// redeclares the topology on a fresh one instead of failing forever on a dead handle.
			await DisposePublishAsync();
			throw;
		}
		finally
		{
			_publishGate.Release();
		}
	}

	public async Task RunConsumerAsync(Func<DeliveryTask, CancellationToken, Task<bool>> handler, CancellationToken ct)
	{
		DeliveryOptions config = options.Value;

		await using IConnection connection = await CreateConnectionAsync(config, "chat-delivery-consumer", ct);
		await using IChannel channel = await connection.CreateChannelAsync(
			new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false), ct);

		await DeclareTopologyAsync(channel, config, ct);
		await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: config.PrefetchCount, global: false, cancellationToken: ct);

		// Completes when the connection goes away or the host is stopping; the hosted service then
		// reconnects with backoff.
		TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.ConnectionShutdownAsync += (_, _) =>
		{
			stopped.TrySetResult();
			return Task.CompletedTask;
		};

		AsyncEventingBasicConsumer consumer = new(channel);
		consumer.ReceivedAsync += async (_, delivery) =>
		{
			bool acknowledge;
			try
			{
				DeliveryTask? task = JsonSerializer.Deserialize<DeliveryTask>(delivery.Body.Span, TaskJson);
				if (task is null)
				{
					// Nothing can ever be done with a payload this consumer cannot read; requeueing it would
					// make it circulate forever. The outbox row it came from stays claimable on its own lease.
					logger.LogError("Delivery consumer received an unreadable task payload; dropping it.");
					acknowledge = true;
				}
				else
				{
					acknowledge = await handler(task, ct);
				}
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Delivery consumer failed to handle a task; returning it to the queue.");
				acknowledge = false;
			}

			try
			{
				if (acknowledge)
				{
					await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: ct);
				}
				else
				{
					await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken: ct);
				}
			}
			catch (Exception ex)
			{
				// The broker will redeliver what was never acknowledged - the handler's own work is already
				// either done or durably recorded, so this costs at most a duplicate.
				logger.LogWarning(ex, "Delivery consumer could not settle a broker message; it will be redelivered.");
			}
		};

		await channel.BasicConsumeAsync(config.LiveQueue, autoAck: false, consumer: consumer, cancellationToken: ct);

		using (ct.Register(() => stopped.TrySetResult()))
		{
			await stopped.Task;
		}
	}

	public async ValueTask DisposeAsync()
	{
		await DisposePublishAsync();
		_publishGate.Dispose();
	}

	private async Task<IChannel> EnsurePublishChannelAsync(DeliveryOptions config, CancellationToken ct)
	{
		if (_publishChannel is { IsOpen: true } open)
		{
			return open;
		}

		await DisposePublishAsync();

		_publishConnection = await CreateConnectionAsync(config, "chat-delivery-publisher", ct);
		_publishChannel = await _publishConnection.CreateChannelAsync(
			new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

		await DeclareTopologyAsync(_publishChannel, config, ct);
		return _publishChannel;
	}

	private static async Task<IConnection> CreateConnectionAsync(DeliveryOptions config, string name, CancellationToken ct)
	{
		ConnectionFactory factory = new()
		{
			Uri = new Uri(config.ConnectionUri!),
			ClientProvidedName = $"{name}:{Environment.MachineName}",
		};

		return await factory.CreateConnectionAsync(ct);
	}

	/// <summary>Idempotent and declared by BOTH sides, so neither a publisher nor a consumer starting first can publish into nothing.</summary>
	private static async Task DeclareTopologyAsync(IChannel channel, DeliveryOptions config, CancellationToken ct)
	{
		await channel.ExchangeDeclareAsync(
			config.Exchange, ExchangeType.Direct, durable: true, autoDelete: false, arguments: null,
			noWait: false, cancellationToken: ct);
		await channel.QueueDeclareAsync(
			config.LiveQueue, durable: true, exclusive: false, autoDelete: false, arguments: null,
			noWait: false, cancellationToken: ct);
		await channel.QueueBindAsync(
			config.LiveQueue, config.Exchange, config.LiveRoutingKey, arguments: null,
			noWait: false, cancellationToken: ct);
	}

	private async Task DisposePublishAsync()
	{
		if (_publishChannel is not null)
		{
			try
			{
				await _publishChannel.DisposeAsync();
			}
			catch (Exception ex)
			{
				logger.LogDebug(ex, "Disposing the delivery publish channel failed; a new one will be created.");
			}

			_publishChannel = null;
		}

		if (_publishConnection is not null)
		{
			try
			{
				await _publishConnection.DisposeAsync();
			}
			catch (Exception ex)
			{
				logger.LogDebug(ex, "Disposing the delivery publish connection failed; a new one will be created.");
			}

			_publishConnection = null;
		}
	}
}
