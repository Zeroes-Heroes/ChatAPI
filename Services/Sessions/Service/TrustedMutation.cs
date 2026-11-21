using System.Security.Claims;
using Database.Context;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Sessions.Interface;
using Services.Sessions.Models;

namespace Services.Sessions.Service;

/// <summary>
/// Shared helper for every entrypoint (HTTP or hub) that mutates state and then publishes/broadcasts the
/// result to someone OTHER than the acting caller - see <see cref="ISessionTrustChecker.CheckAndLockDeviceForMutationAsync"/>'s
/// own doc comment for why a guard evaluated only on entry (plain <c>CheckAsync</c>, or a hub's one-time
/// <c>[Authorize]</c>) cannot by itself stop a request admitted a moment before a concurrent revoke's
/// commit from completing its own write afterward. Opens one transaction on the SAME <see cref="AppDbContext"/>
/// the caller's own mutation uses, takes the device(+session) lock, and - only if the caller is still
/// trusted under it - runs the supplied mutation and commits; otherwise rolls back without running it.
/// Used by <see cref="Hubs.BaseHub"/>'s <c>SendMessage</c>/<c>ChatEntered</c>/<c>OnConnectedAsync</c> and
/// by the HTTP mutation+broadcast entrypoints (<c>ChatService.CreateChat</c>,
/// <c>FriendshipService.AddFriend</c>/<c>RespondToFriendRequest</c>) - one focused helper, not a
/// per-call-site copy of the same transaction/lock/rollback boilerplate.
/// </summary>
public static class TrustedMutation
{
	public static async Task<TrustCheckResult> RunAsync(
		AppDbContext db, ISessionTrustChecker trustChecker, ClaimsPrincipal principal, Func<Task> mutateAndSave, CancellationToken ct = default)
	{
		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
		TrustCheckResult trust = await trustChecker.CheckAndLockDeviceForMutationAsync(principal, ct);
		if (!trust.IsTrusted)
		{
			await tx.RollbackAsync(ct);
			return trust;
		}

		await mutateAndSave();
		await tx.CommitAsync(ct);
		return trust;
	}
}
