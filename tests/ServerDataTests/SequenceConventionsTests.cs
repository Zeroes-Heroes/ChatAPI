using Database.Sequencing;
using Xunit;

namespace ServerDataTests;

/// <summary>
/// Pure unit tests (no database needed) for the OwnSendThroughSequence derivation: it must stay a
/// deterministic, immutable function of a message's own MessageSequence so that retrying an
/// already-accepted operation - which returns the original persisted result rather than
/// recomputing anything - always reports the same boundary.
/// </summary>
public class SequenceConventionsTests
{
	[Fact]
	public void OwnSendThroughSequence_ForFirstMessageInChat_IsEmptyStreamPosition()
	{
		long result = SyncSequenceConventions.OwnSendThroughSequence(SyncSequenceConventions.FirstSequence);

		Assert.Equal(SyncSequenceConventions.EmptyStreamPosition, result);
	}

	[Theory]
	[InlineData(1, 0)]
	[InlineData(2, 1)]
	[InlineData(5, 4)]
	[InlineData(9223372036854775807, 9223372036854775806)]
	public void OwnSendThroughSequence_IsMessageSequenceMinusOne(long messageSequence, long expected)
	{
		Assert.Equal(expected, SyncSequenceConventions.OwnSendThroughSequence(messageSequence));
	}

	/// <summary>
	/// The original acceptance boundary invariant on retry: because the derivation depends only on
	/// the message's own already-immutable MessageSequence, computing it twice for the same
	/// message (as a retried operation lookup would) yields the exact same value every time.
	/// </summary>
	[Fact]
	public void OwnSendThroughSequence_IsDeterministic_SoRetriesObserveTheSameBoundary()
	{
		const long messageSequence = 42;

		long first = SyncSequenceConventions.OwnSendThroughSequence(messageSequence);
		long second = SyncSequenceConventions.OwnSendThroughSequence(messageSequence);

		Assert.Equal(first, second);
	}

	[Fact]
	public void OwnSendThroughSequence_BelowFirstSequence_ThrowsRatherThanInventingANegativeSentinel()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => SyncSequenceConventions.OwnSendThroughSequence(0));
		Assert.Throws<ArgumentOutOfRangeException>(() => SyncSequenceConventions.OwnSendThroughSequence(-1));
	}
}
