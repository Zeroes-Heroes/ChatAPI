using Services.Delivery.Interface;

namespace Services.Delivery.Service;

/// <summary>
/// See <see cref="IOutboxPublishSignal"/>. One pending signal is enough: the publisher claims a whole
/// batch and loops while it keeps filling it, so coalescing many accepted messages into one wake-up
/// loses nothing.
/// </summary>
public sealed class OutboxPublishSignal : IOutboxPublishSignal
{
	private readonly SemaphoreSlim _signal = new(0, 1);

	public void Notify()
	{
		try
		{
			_signal.Release();
		}
		catch (SemaphoreFullException)
		{
			// A wake-up is already pending; a second one would wake the loop twice for one batch.
		}
	}

	public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
