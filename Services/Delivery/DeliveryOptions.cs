namespace Services.Delivery;

/// <summary>
/// Stage 12 (plan/12-outbox-delivery.md) configuration of the post-commit delivery path. Bound from the
/// "Delivery" configuration section (env vars <c>Delivery__ConnectionUri</c>, <c>Delivery__Exchange</c>,
/// ...), like <see cref="Services.Keys.MasterSecretOptions"/> and unlike the flat legacy keys, so it
/// cannot collide with another top-level name.
///
/// <see cref="ConnectionUri"/> empty means this deployment has no broker configured: the publisher and
/// the consumer then do not start at all and say so once, instead of failing every poll. That is the
/// honest state - nothing is delivered live, and every message stays recoverable through sync - not a
/// silent fallback to some other delivery path.
/// </summary>
public sealed class DeliveryOptions
{
	/// <summary>AMQP URI of the shared RabbitMQ, e.g. <c>amqp://user:secret@rabbitmq:5672/</c>. Null/empty disables the delivery workers.</summary>
	public string? ConnectionUri { get; set; }

	/// <summary>Durable direct exchange every delivery task is published to.</summary>
	public string Exchange { get; set; } = "chat.delivery";

	/// <summary>The shared live-delivery queue every replica competes on. The push queue is deliberately separate and belongs to the push stage.</summary>
	public string LiveQueue { get; set; } = "chat.delivery.live";

	/// <summary>Routing key binding <see cref="LiveQueue"/> to <see cref="Exchange"/>; a publish that routes nowhere fails rather than being confirmed.</summary>
	public string LiveRoutingKey { get; set; } = "live";

	/// <summary>How many outbox rows one claim takes. Bounds a single scope's work, per MESSAGE_SYNC_ANALYSIS.md's "Фоновата работа създава и освобождава scope за ограничена порция".</summary>
	public int ClaimBatchSize { get; set; } = 50;

	/// <summary>How long a claimed row stays owned by its publisher. A publisher that dies mid-batch loses its rows after this, and another replica re-claims them.</summary>
	public int LeaseSeconds { get; set; } = 60;

	/// <summary>Upper bound on how long the publisher sleeps when nothing signalled it. Latency comes from the in-process signal; this poll is what makes another replica's rows and a crashed publisher's lease recoverable.</summary>
	public int PollSeconds { get; set; } = 5;

	/// <summary>First retry delay after a transient failure; doubled per attempt up to <see cref="RetryMaxSeconds"/>.</summary>
	public int RetryBaseSeconds { get; set; } = 2;

	public int RetryMaxSeconds { get; set; } = 300;

	/// <summary>After this many attempts a row stops being retried and is kept for diagnostics/explicit re-release ("необработваемите задачи се пазят за диагностика и повторно пускане").</summary>
	public int MaxAttempts { get; set; } = 12;

	/// <summary>Unacknowledged broker messages one consumer may hold at a time.</summary>
	public ushort PrefetchCount { get; set; } = 20;

	public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionUri);
}
