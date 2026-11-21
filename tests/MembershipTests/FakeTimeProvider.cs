namespace MembershipTests;

/// <summary>Minimal controllable <see cref="TimeProvider"/> - the membership writer takes one, never <see cref="DateTime.UtcNow"/>, so history bounds and freeze times are deterministic here.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
	private DateTimeOffset _now = start;

	public override DateTimeOffset GetUtcNow() => _now;

	public void Advance(TimeSpan by) => _now += by;
}
