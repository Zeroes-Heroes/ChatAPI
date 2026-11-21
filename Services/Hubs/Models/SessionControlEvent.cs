namespace Services.Hubs.Models;

/// <summary>
/// Legacy-hub (MessagePack, CustomResolver) shaped event for the best-effort live session-revocation
/// notice - see Services/Sessions/Interface/ISessionRevocationNotifier.cs. Deliberately a separate,
/// simpler type from docs/sync-contract-v1.md's own (JSON, unpublished) SessionControlEvent: that one
/// is the stage-01 wire contract for the "separately versioned hub" the docs explicitly say is a
/// future transport, not this stage's actual delivery mechanism - this stage still only has the
/// existing MessagePack BaseHub to notify real connected clients through, so it gets its own
/// MessagePack-friendly POCO instead of conflating an unpublished JSON contract with the hub protocol
/// actually in use today.
/// </summary>
public class SessionControlEvent
{
	public SessionControlEvent(string sessionId, string reason)
	{
		SessionId = sessionId;
		Reason = reason;
	}

	public SessionControlEvent() { }

	public string SessionId { get; set; } = null!;

	/// <summary>One of docs/sync-contract-v1.md's SessionControlReason names ("Revoked", "Expired", "Draining", "DependencyUnavailable"), as plain text - this event's only consumer in this stage is "disconnect yourself now", so the exact reason is informational.</summary>
	public string Reason { get; set; } = null!;
}
