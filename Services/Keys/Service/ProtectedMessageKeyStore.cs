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
}
