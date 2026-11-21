namespace Services.Sessions.Models;

/// <summary>
/// The fields <see cref="Interface.ISessionConnectionRegistry"/> needs to later decide whether a
/// registered connection is still allowed to stay open, and to actually close it if not - captured
/// once, at <c>OnConnectedAsync</c> time, from the same claims <see cref="Interface.ISessionTrustChecker"/>
/// already validated for the connect itself.
/// </summary>
/// <param name="ConnectionId">The SignalR connection id (registry key) - see <see cref="Interface.ISessionConnectionRegistry.Unregister"/>.</param>
/// <param name="AccountId">AccountContextId, for the bulk revalidation query.</param>
/// <param name="SessionId">SessionId, for direct same-replica abort-by-session (<see cref="Interface.ISessionConnectionRegistry.AbortSession"/>).</param>
/// <param name="DeviceRegistrationId">DeviceRegistrationId, for the bulk revalidation query.</param>
/// <param name="TrustVersionAtConnect">The device's TrustVersion at connect time - a connection registered under one epoch that no longer matches the device's live TrustVersion is exactly as stale as an HTTP request would be.</param>
/// <param name="ExpiresAtUtc">The access token's own <c>exp</c> claim at connect time - checked every maintenance tick independent of any DB round trip (see <see cref="Service.SessionMaintenanceHostedService"/>'s own doc comment), since an idle connection's token can expire without ever being revoked.</param>
/// <param name="ConnectedAtUtc">When this connection was registered - the fallback baseline a never-yet-DB-reverified connection is measured against for <see cref="Service.SessionMaintenanceHostedService"/>'s fail-closed sustained-outage bound.</param>
/// <param name="Abort">Closes this exact connection - <c>HubCallerContext.Abort()</c> for the connection that registered this entry; safe to call more than once.</param>
public sealed record LiveSessionConnection(
	string ConnectionId,
	string AccountId,
	string SessionId,
	string DeviceRegistrationId,
	long TrustVersionAtConnect,
	DateTimeOffset ExpiresAtUtc,
	DateTimeOffset ConnectedAtUtc,
	Action Abort);
