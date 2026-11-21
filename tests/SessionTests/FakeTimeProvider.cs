namespace SessionTests;

/// <summary>Minimal controllable <see cref="TimeProvider"/> for deterministic expiry/replay-window tests - every session-module service that needs "now" takes a <see cref="TimeProvider"/>, never <see cref="DateTime.UtcNow"/> directly, specifically so tests can do this.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
	private DateTimeOffset _now = start;

	public override DateTimeOffset GetUtcNow() => _now;

	public void Advance(TimeSpan by) => _now += by;
}
