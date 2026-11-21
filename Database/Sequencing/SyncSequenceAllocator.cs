using System.Data;
using System.Data.Common;
using Database.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Database.Sequencing;

/// <summary>
/// Allocates chat message/change sequences and account catalog sequences using a single atomic
/// <c>UPDATE ... RETURNING</c> against the counter row, instead of a database sequence/identity
/// column.
///
/// Why not a sequence/identity: with <c>nextval()</c> or an identity column, transaction T1 can
/// grab position 5 and then be outlived by transaction T2, which grabs position 6 and commits
/// first. A reader polling "give me everything committed up to the highest position I've seen"
/// would then observe position 6 while position 5 is still in flight (or about to roll back) - a
/// permanent, unrecoverable hole in the visible order. Sequence/identity values are only
/// guaranteed to be generated in order, not committed in order.
///
/// Why this works: <c>UPDATE counters SET n = n + 1 WHERE id = @id RETURNING n - 1</c> takes a
/// row lock on the counter that is held for the lifetime of the allocating transaction (it is
/// released on COMMIT or ROLLBACK, not when the statement finishes). A second transaction trying
/// to allocate from the same counter blocks on that same UPDATE until the first transaction ends.
/// If the first transaction commits, the second one's UPDATE resumes against the now-committed
/// value and allocates the very next position - so position N+1 can never even be allocated while
/// position N is still uncommitted, which is a stronger guarantee than "generated in order". If
/// the first transaction rolls back, the counter reverts (the UPDATE is undone too), so the
/// number is reused rather than leaving a permanent gap.
///
/// Precondition: the caller must have already started an explicit transaction on this DbContext
/// (via <c>Database.BeginTransactionAsync</c>) before calling any Allocate* method, and must keep
/// that same connection/transaction open until it commits whatever the allocated sequence is used
/// for. Allocating outside an explicit transaction would release the row lock immediately,
/// defeating the whole point.
/// </summary>
public static class SyncSequenceAllocator
{
	/// <summary>
	/// Allocates the next message sequence and the next change sequence for a chat together, from
	/// the same counter row, so a new message and a concurrent non-message chat change (e.g. a
	/// membership update) are forced through the same lock and get an unambiguous relative order.
	/// </summary>
	public static Task<(long MessageSequence, long ChangeSequence)> AllocateMessageAndChangeSequenceAsync(
		AppDbContext dbContext, string chatId, CancellationToken cancellationToken = default) =>
		ExecutePairAsync(
			dbContext,
			"""
			UPDATE "SyncChatSequenceCounters"
			SET "NextMessageSequence" = "NextMessageSequence" + 1, "NextChangeSequence" = "NextChangeSequence" + 1
			WHERE "ChatId" = @id
			RETURNING "NextMessageSequence" - 1, "NextChangeSequence" - 1
			""",
			chatId, cancellationToken);

	/// <summary>Allocates only the next change sequence for a chat (e.g. for a membership/unread/metadata change that is not itself a message).</summary>
	public static Task<long> AllocateChangeSequenceAsync(
		AppDbContext dbContext, string chatId, CancellationToken cancellationToken = default) =>
		ExecuteSingleAsync(
			dbContext,
			"""
			UPDATE "SyncChatSequenceCounters"
			SET "NextChangeSequence" = "NextChangeSequence" + 1
			WHERE "ChatId" = @id
			RETURNING "NextChangeSequence" - 1
			""",
			chatId, cancellationToken);

	/// <summary>Allocates the next per-account catalog sequence, a distinct ordering domain from any chat's change sequence.</summary>
	public static Task<long> AllocateCatalogSequenceAsync(
		AppDbContext dbContext, string accountId, CancellationToken cancellationToken = default) =>
		ExecuteSingleAsync(
			dbContext,
			"""
			UPDATE "SyncAccountCatalogCounters"
			SET "NextCatalogSequence" = "NextCatalogSequence" + 1
			WHERE "AccountId" = @id
			RETURNING "NextCatalogSequence" - 1
			""",
			accountId, cancellationToken);

	private static async Task<long> ExecuteSingleAsync(AppDbContext dbContext, string sql, string id, CancellationToken cancellationToken)
	{
		await using DbCommand command = CreateCommand(dbContext, sql, id);
		object? result = await command.ExecuteScalarAsync(cancellationToken);
		return result is null
			? throw new InvalidOperationException("Sequence counter row not found; it must be created alongside its owning chat/account.")
			: (long)result;
	}

	private static async Task<(long, long)> ExecutePairAsync(AppDbContext dbContext, string sql, string id, CancellationToken cancellationToken)
	{
		await using DbCommand command = CreateCommand(dbContext, sql, id);
		await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		if (!await reader.ReadAsync(cancellationToken))
			throw new InvalidOperationException("Sequence counter row not found; it must be created alongside its owning chat.");

		return (reader.GetInt64(0), reader.GetInt64(1));
	}

	private static DbCommand CreateCommand(AppDbContext dbContext, string sql, string id)
	{
		DbTransaction transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
			?? throw new InvalidOperationException(
				$"{nameof(SyncSequenceAllocator)} requires an explicit transaction started via Database.BeginTransactionAsync; " +
				"allocating outside one would release the counter row lock before the caller's write commits.");

		DbCommand command = dbContext.Database.GetDbConnection().CreateCommand();
		command.CommandText = sql;
		command.CommandType = CommandType.Text;
		command.Transaction = transaction;

		DbParameter parameter = command.CreateParameter();
		parameter.ParameterName = "id";
		parameter.Value = id;
		command.Parameters.Add(parameter);

		return command;
	}
}
