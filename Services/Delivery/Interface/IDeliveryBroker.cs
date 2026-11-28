using Services.Delivery.Models;

namespace Services.Delivery.Interface;

/// <summary>
/// The broker seam, and the ONLY place RabbitMQ.Client is used. Everything that decides anything -
/// claiming, authorizing, building the live event - is above this interface and is exercised without a
/// broker; this is the one collaborator a test process genuinely cannot have.
///
/// <see cref="PublishAsync"/> completes only after the broker CONFIRMED the publish and the message
/// really routed to the live queue (MESSAGE_SYNC_ANALYSIS.md: "Publisher отбелязва успех след broker
/// confirm и проверено маршрутизиране"); anything else throws, and the caller returns the row for
/// another attempt.
/// </summary>
public interface IDeliveryBroker
{
	Task PublishAsync(DeliveryTask task, CancellationToken ct = default);

	/// <summary>
	/// Consumes the live queue until <paramref name="ct"/> is cancelled or the connection drops,
	/// invoking <paramref name="handler"/> per task. The handler answers whether the broker message may
	/// be acknowledged: true once the task was executed OR its responsibility was durably handed back to
	/// PostgreSQL, false to return it to the queue ("Consumer потвърждава след изпълнение или устойчиво
	/// прехвърляне на отговорността").
	/// </summary>
	Task RunConsumerAsync(Func<DeliveryTask, CancellationToken, Task<bool>> handler, CancellationToken ct);
}
