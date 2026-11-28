using System.Security.Cryptography;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Sync.Contracts;
using Services.Sync.Interface;

namespace Services.Sync.Service;

/// <summary>See <see cref="ISyncCursorStore"/>.</summary>
public sealed class SyncCursorStore(AppDbContext db) : ISyncCursorStore
{
	/// <summary>"opaque random 256-bit tokens" - 32 bytes from the cryptographic RNG, base64url, nothing derived from the position it names.</summary>
	private const int TokenBytes = 32;

	public SyncCursorEntity Mint(
		SyncSessionEntity session, SyncCursorSurface surface, SyncStreamKind streamKind,
		string? chatId, string? membershipId, long fromSequence, long upperBoundSequence, DateTime nowUtc)
	{
		SyncCursorEntity row = new(
			Base64Url.Encode(RandomNumberGenerator.GetBytes(TokenBytes)),
			session.Id,
			session.AccountId,
			session.DeviceRegistrationId,
			session.LocalInstanceId,
			surface,
			streamKind,
			session.RightsVersion,
			session.SnapshotId,
			fromSequence,
			upperBoundSequence,
			nowUtc,
			// "Cursor lifetime follows sync session": one expiry, so an expired session can never leave a
			// usable cursor behind, and an expired cursor never outlives the rights it was bound to.
			session.ExpiresAtUtc)
		{
			ChatId = chatId,
			MembershipId = membershipId,
		};

		db.SyncCursors.Add(row);
		return row;
	}

	public async Task<SyncCursorEntity?> ResolveAsync(string? token, DateTime nowUtc, CancellationToken ct = default)
	{
		// Shape-checked before it reaches the database: a token is always 43 canonical base64url
		// characters, so anything else is answered without a query.
		if (token is null || token.Length > SyncProtocol.MaxCursorBytes || !Base64Url.TryDecode(token, out byte[] raw) || raw.Length != TokenBytes)
		{
			return null;
		}

		SyncCursorEntity? row = await db.SyncCursors.FirstOrDefaultAsync(c => c.Id == token, ct);
		return row is null || row.ExpiresAtUtc <= nowUtc ? null : row;
	}
}
