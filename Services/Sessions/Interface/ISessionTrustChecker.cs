using System.Security.Claims;
using Services.Sessions.Models;

namespace Services.Sessions.Interface;

/// <summary>
/// The one shared "is this app token still currently trusted right now" check, per
/// plan/05-device-sessions.md: "Current trust/session/account state checked EVERY HTTP+SignalR
/// operation, not merely on connect/token issuance." Used by exactly two call sites - the default
/// HTTP authorization policy's requirement handler (one check per HTTP request) and the SignalR hub
/// filter (one check per hub invocation, including OnConnectedAsync, since a hub connection's
/// ASP.NET Core authorization otherwise only runs once at connect) - so there is one implementation of
/// "current trust" to keep correct, not two independently-maintained copies. Signature/issuer/audience
/// are the JwtBearer handshake/HTTP auth pipeline's responsibility, never re-verified here. <c>exp</c>
/// is NOT solely that pipeline's responsibility, though: a SignalR hub only runs authentication/
/// authorization once, at connect, so nothing else ever re-validates a connect-time-valid token's
/// expiry for a later frame on that same long-lived connection - this checker re-checks <c>exp</c>
/// itself, using an injected <see cref="TimeProvider"/>, on every single invocation. It also binds the
/// token's claimed identity (legacy subject, LocalInstanceId) back to the server's own authoritative
/// session/account rows, not only TrustState/TrustVersion - see <see cref="Service.SessionTrustChecker"/>'s
/// own doc comment for the exact fields.
/// </summary>
public interface ISessionTrustChecker
{
	Task<TrustCheckResult> CheckAsync(ClaimsPrincipal principal, CancellationToken ct = default);

	/// <summary>
	/// Same check as <see cref="CheckAsync"/>, but first takes a <c>FOR UPDATE</c> lock on the caller's
	/// device registration row AND the caller's own session row, participating in the CALLER's OWN
	/// ambient transaction (the caller must already have one open - see <see cref="Services.Hubs.BaseHub.SendMessage"/>
	/// and the status writes behind <see cref="Services.Hubs.BaseHub.ChatEntered"/> /
	/// <see cref="Services.Hubs.BaseHub.ReportMessageReceipts"/> (<see cref="Services.Statuses.Interface.IMessageStatusService"/>),
	/// the hub methods that mutate state and then broadcast the result to
	/// ANOTHER user's connections, plus the equivalent HTTP mutation+broadcast entrypoints
	/// (<c>ChatService.CreateChat</c>, <c>FriendshipService.AddFriend</c>/<c>RespondToFriendRequest</c>,
	/// via <see cref="Service.TrustedMutation"/>) - <see cref="Services.Hubs.BaseHub.ChatExited"/>/
	/// <see cref="Services.Hubs.BaseHub.MessageStatusUpdateReceived"/> only ever mutate/broadcast
	/// information back to the CALLER's own other connections or purely local cache bookkeeping, never a
	/// write another user observes as this caller's, so they do not need this treatment).
	/// A guard evaluated merely on entry (plain <see cref="CheckAsync"/>, or SignalR's own one-time
	/// <c>[Authorize]</c>, or an HTTP request's own authorization middleware) cannot by itself stop a
	/// request that was legitimately admitted BEFORE a revoke from completing its own write AFTER that
	/// revoke's commit - the two are independent operations with no shared serialization point. Locking
	/// the exact device row <see cref="Service.DeviceTrustService.RevokeDeviceAsync"/> also locks (after
	/// its own account lock - see that method's doc comment for the full ordering) means the two
	/// genuinely serialize: either this call's lock acquisition blocks until a concurrent revoke of the
	/// SAME device commits or rolls back (after which this call re-reads the now-current, post-revoke
	/// state and correctly fails), or this call's own transaction commits (and is visible) strictly
	/// before any later revoke of that device can proceed past its own lock acquisition. The ADDITIONAL
	/// session-row lock closes a separate gap: <see cref="Service.RefreshTokenService.RotateAsync"/>'s
	/// reuse-detection path can end a session WITHOUT ever revoking (or touching) the device row at all -
	/// a device-only lock would not serialize against that; locking the session row too (consistent
	/// account-&gt;device-&gt;session ordering with <see cref="Service.DeviceTrustService.RevokeDeviceAsync"/>/
	/// <see cref="Service.RefreshTokenService.RotateAsync"/>, so this can never deadlock against either)
	/// does.
	/// </summary>
	Task<TrustCheckResult> CheckAndLockDeviceForMutationAsync(ClaimsPrincipal principal, CancellationToken ct = default);
}
