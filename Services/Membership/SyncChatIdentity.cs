using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Database.Context;
using Microsoft.EntityFrameworkCore;

namespace Services.Membership;

/// <summary>
/// The two primitives every membership path shares: how a legacy chat id becomes the sync chat id, and
/// the locks that serialize chat-scoped work.
/// </summary>
internal static class SyncChatIdentity
{
	/// <summary>
	/// docs/sync-contract-v1.md: "Existing numeric entity IDs become decimal strings." A group therefore
	/// keeps one ChatId through every composition change without a mapping table.
	/// </summary>
	public static string IdOf(int legacyChatId) => legacyChatId.ToString(CultureInfo.InvariantCulture);

	/// <summary>
	/// Takes the chat's counter row - the row <see cref="Database.Sequencing.SyncSequenceAllocator"/>
	/// allocates every chat change from - with <c>FOR UPDATE</c> in the caller's own open transaction.
	/// This is the single serialization point between a rights change and a chat write
	/// (plan/07-membership.md). Returns false when the chat has no projection yet, so there is nothing
	/// to lock.
	/// </summary>
	public static async Task<bool> TryLockChatAsync(AppDbContext db, string chatId, CancellationToken ct)
	{
		List<Database.Entities.Sync.SyncChatSequenceCounterEntity> locked = await db.SyncChatSequenceCounters
			.FromSqlInterpolated($"""SELECT * FROM "SyncChatSequenceCounters" WHERE "ChatId" = {chatId} FOR UPDATE""")
			.AsNoTracking()
			.ToListAsync(ct);

		return locked.Count == 1;
	}

	/// <summary>
	/// Transaction-scoped advisory lock over a stable 64-bit key, for the two serialization points that
	/// have no row of their own to lock: creating a chat for an exact participant set (two concurrent
	/// creations of the same set must not both pass the duplicate check) and projecting a pre-stage-07
	/// legacy chat on first use (two concurrent mutations must not both insert the same SyncChats row).
	/// A key collision only costs extra serialization, never correctness, and PostgreSQL advisory locks
	/// are cluster-wide, so this holds across API replicas.
	/// </summary>
	public static Task LockKeyAsync(AppDbContext db, string key, CancellationToken ct)
	{
		long lockKey = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0);
		return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
	}

	/// <summary>Advisory-lock key for "a chat whose exact active participant set is this one", order-independent.</summary>
	public static string MemberSetKey(IEnumerable<int> legacyUserIds) =>
		"chat-member-set:" + string.Join(',', legacyUserIds.Distinct().Order());

	/// <summary>Advisory-lock key for projecting one legacy chat into the sync model.</summary>
	public static string ProjectionKey(int legacyChatId) => $"chat-projection:{IdOf(legacyChatId)}";
}
