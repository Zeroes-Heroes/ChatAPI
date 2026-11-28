using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Keys.Interface;

namespace Services.Keys.Service;

public sealed class ProtectedMessageKeyStore : IProtectedMessageKeyStore
{
	public void Stage(AppDbContext db, SyncMessageKeyEntity entity) => db.SyncMessageKeys.Add(entity);

	public Task<SyncMessageKeyEntity?> FindAsync(AppDbContext db, string messageId, CancellationToken cancellationToken = default) =>
		db.SyncMessageKeys.FirstOrDefaultAsync(k => k.MessageId == messageId, cancellationToken);

	public async Task<IReadOnlyDictionary<string, SyncMessageKeyEntity>> FindManyAsync(
		AppDbContext db, IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
	{
		if (messageIds.Count == 0)
		{
			return new Dictionary<string, SyncMessageKeyEntity>(StringComparer.Ordinal);
		}

		string[] ids = [.. messageIds.Distinct(StringComparer.Ordinal)];
		return await db.SyncMessageKeys.AsNoTracking()
			.Where(k => ids.Contains(k.MessageId))
			.ToDictionaryAsync(k => k.MessageId, StringComparer.Ordinal, cancellationToken);
	}
}
