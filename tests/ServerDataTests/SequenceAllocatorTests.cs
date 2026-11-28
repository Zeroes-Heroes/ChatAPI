using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Database.Sequencing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ServerDataTests;

/// <summary>
/// Proves the commit-order invariant against real PostgreSQL, not just that the final numbers
/// come out right: a later chat change can never become visible as committed while an earlier one
/// for the same chat is still open.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SequenceAllocatorTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task AllocateChangeSequence_WithoutTransaction_Throws()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncChatEntity chat = await TestData.CreateChatAsync(db);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => SyncSequenceAllocator.AllocateChangeSequenceAsync(db, chat.Id));
	}

	/// <summary>
	/// T1 allocates and holds its transaction open. T2's allocation attempt on the same chat must
	/// not even return (let alone let T2 commit) until T1 ends. This is the actual mechanism, not
	/// an inference from final state.
	/// </summary>
	[Fact]
	public async Task SecondAllocation_BlocksUntilFirstTransactionEnds()
	{
		using AppDbContext db1 = fixture.CreateContext();
		SyncChatEntity chat = await TestData.CreateChatAsync(db1);

		await using var tx1 = await db1.Database.BeginTransactionAsync();
		(long messageSeq1, long changeSeq1) = await SyncSequenceAllocator.AllocateMessageAndChangeSequenceAsync(db1, chat.Id);
		Assert.Equal(1, messageSeq1);
		Assert.Equal(1, changeSeq1);

		using AppDbContext db2 = fixture.CreateContext();
		await using var tx2 = await db2.Database.BeginTransactionAsync();
		Task<(long MessageSequence, long ChangeSequence)> secondAllocation =
			SyncSequenceAllocator.AllocateMessageAndChangeSequenceAsync(db2, chat.Id);

		Task completedFirst = await Task.WhenAny(secondAllocation, Task.Delay(TimeSpan.FromMilliseconds(500)));
		Assert.False(ReferenceEquals(completedFirst, secondAllocation), "Second allocation must stay blocked while the first transaction is still open.");

		await tx1.CommitAsync();

		(long messageSeq2, long changeSeq2) = await secondAllocation.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(2, messageSeq2);
		Assert.Equal(2, changeSeq2);

		await tx2.CommitAsync();
	}

	/// <summary>If the first transaction rolls back instead of committing, the number is reclaimed rather than leaving a permanent hole.</summary>
	[Fact]
	public async Task RolledBackAllocation_IsReusedByNextAllocation()
	{
		using AppDbContext db1 = fixture.CreateContext();
		SyncChatEntity chat = await TestData.CreateChatAsync(db1);

		await using (var tx1 = await db1.Database.BeginTransactionAsync())
		{
			(long messageSeq, long changeSeq) = await SyncSequenceAllocator.AllocateMessageAndChangeSequenceAsync(db1, chat.Id);
			Assert.Equal(1, messageSeq);
			Assert.Equal(1, changeSeq);
			await tx1.RollbackAsync();
		}

		using AppDbContext db2 = fixture.CreateContext();
		await using var tx2 = await db2.Database.BeginTransactionAsync();
		(long messageSeq2, long changeSeq2) = await SyncSequenceAllocator.AllocateMessageAndChangeSequenceAsync(db2, chat.Id);
		Assert.Equal(1, messageSeq2);
		Assert.Equal(1, changeSeq2);
		await tx2.CommitAsync();
	}

	/// <summary>
	/// Many real concurrent transactions race for the same chat's counter, each independently
	/// committing or rolling back. Afterwards the committed change sequences must be exactly
	/// {FirstSequence, ..., FirstSequence + committedCount - 1} with no duplicates and no permanent gaps.
	/// </summary>
	[Fact]
	public async Task ConcurrentAllocations_ProduceContiguousCommittedSequenceWithNoDuplicates()
	{
		using AppDbContext setupDb = fixture.CreateContext();
		SyncChatEntity chat = await TestData.CreateChatAsync(setupDb);

		const int attempts = 24;
		Task<long?>[] tasks = Enumerable.Range(0, attempts)
			.Select(i => AttemptOneAllocationAsync(chat.Id, commit: i % 3 != 0))
			.ToArray();

		long?[] results = await Task.WhenAll(tasks);
		long[] committed = results.Where(r => r.HasValue).Select(r => r!.Value).OrderBy(v => v).ToArray();

		Assert.Equal(committed.Length, committed.Distinct().Count());
		// Real sequences start at SyncSequenceConventions.FirstSequence (1), not 0.
		Assert.Equal(Enumerable.Range((int)SyncSequenceConventions.FirstSequence, committed.Length).Select(i => (long)i), committed);

		using AppDbContext verifyDb = fixture.CreateContext();
		SyncChatSequenceCounterEntity counter = await verifyDb.SyncChatSequenceCounters.SingleAsync(c => c.ChatId == chat.Id);
		Assert.Equal(SyncSequenceConventions.FirstSequence + committed.Length, counter.NextChangeSequence);
	}

	private async Task<long?> AttemptOneAllocationAsync(string chatId, bool commit)
	{
		using AppDbContext db = fixture.CreateContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		long changeSequence = await SyncSequenceAllocator.AllocateChangeSequenceAsync(db, chatId);

		if (!commit)
		{
			await tx.RollbackAsync();
			return null;
		}

		db.SyncChatChanges.Add(new SyncChatChangeEntity(chatId, changeSequence, SyncChangeKind.Membership, TestData.NewId("ref"), DateTime.UtcNow));
		await db.SaveChangesAsync();
		await tx.CommitAsync();
		return changeSequence;
	}

	/// <summary>The account catalog counter is a distinct ordering domain and uses the same safe-allocation primitive.</summary>
	[Fact]
	public async Task CatalogSequenceAllocator_SerializesPerAccount()
	{
		using AppDbContext setupDb = fixture.CreateContext();
		SyncAccountEntity account = await TestData.CreateAccountAsync(setupDb);

		const int attempts = 10;
		Task<long>[] tasks = Enumerable.Range(0, attempts).Select(_ => AllocateCatalogSequenceAsync(account.Id)).ToArray();
		long[] results = await Task.WhenAll(tasks);

		Assert.Equal(attempts, results.Distinct().Count());
		Assert.Equal(Enumerable.Range((int)SyncSequenceConventions.FirstSequence, attempts).Select(i => (long)i).OrderBy(v => v), results.OrderBy(v => v));
	}

	private async Task<long> AllocateCatalogSequenceAsync(string accountId)
	{
		using AppDbContext db = fixture.CreateContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		long sequence = await SyncSequenceAllocator.AllocateCatalogSequenceAsync(db, accountId);
		await tx.CommitAsync();
		return sequence;
	}

	/// <summary>
	/// Finding 1: a freshly created chat/account's counter already holds the first real sequence
	/// (1), never the EmptyStreamPosition sentinel (0) - 0 is reserved exclusively for "nothing
	/// has been applied/sent in this stream yet" (e.g. a Resume position before anything exists).
	/// </summary>
	[Fact]
	public async Task FreshChatAndAccountCounters_SeedAtFirstSequence_NotAtEmptyStreamPosition()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncChatEntity chat = await TestData.CreateChatAsync(db);
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);

		SyncChatSequenceCounterEntity chatCounter = await db.SyncChatSequenceCounters.SingleAsync(c => c.ChatId == chat.Id);
		SyncAccountCatalogCounterEntity catalogCounter = await db.SyncAccountCatalogCounters.SingleAsync(c => c.AccountId == account.Id);

		Assert.Equal(SyncSequenceConventions.FirstSequence, chatCounter.NextMessageSequence);
		Assert.Equal(SyncSequenceConventions.FirstSequence, chatCounter.NextChangeSequence);
		Assert.Equal(SyncSequenceConventions.FirstSequence, catalogCounter.NextCatalogSequence);
		Assert.NotEqual(SyncSequenceConventions.EmptyStreamPosition, chatCounter.NextMessageSequence);
	}

	/// <summary>
	/// End-to-end proof of the first-message edge case called out in the review: the first message
	/// ever allocated in a brand-new chat (i.e. after an empty Resume reporting AppliedThrough=0)
	/// gets MessageSequence=1 (not 0, so a ">reportedPosition" catch-up query never skips it), and
	/// its derived OwnSendThroughSequence is 0 - EmptyStreamPosition, correctly meaning "no older
	/// message exists to mark", not an invented sentinel.
	/// </summary>
	[Fact]
	public async Task FirstMessageInNewChat_GetsFirstSequence_AndOwnSendThroughSequenceIsEmptyStreamPosition()
	{
		using AppDbContext db = fixture.CreateContext();
		SyncChatEntity chat = await TestData.CreateChatAsync(db);

		await using var tx = await db.Database.BeginTransactionAsync();
		(long messageSequence, long changeSequence) = await SyncSequenceAllocator.AllocateMessageAndChangeSequenceAsync(db, chat.Id);
		await tx.CommitAsync();

		Assert.Equal(SyncSequenceConventions.FirstSequence, messageSequence);
		Assert.Equal(SyncSequenceConventions.FirstSequence, changeSequence);
		Assert.Equal(SyncSequenceConventions.EmptyStreamPosition, SyncSequenceConventions.OwnSendThroughSequence(messageSequence));
	}
}
