namespace Services.Sessions.Models;

/// <summary>
/// What the caller claims about the approval it is presenting at a registration commit. Replaces the
/// earlier loose <c>(string? approvedByDeviceRegistrationId, bool hasValidApproval)</c> parameter pair
/// on <see cref="Interface.IDeviceTrustService.TryTrustPendingDeviceAsync"/>: the three facts travel
/// together or not at all, so a caller cannot supply an approver without also naming the exact session
/// it came from, and <see cref="SignatureVerified"/> can never drift away from the device it refers to.
///
/// None of these fields is authority by itself (docs/sync-contract-v1.md: "Validate trust at commit;
/// supplied ApprovingSession is not authority") - <see cref="Interface.IDeviceTrustService"/> re-reads
/// the approver's trust state and this session's liveness under row locks before accepting any of it.
/// </summary>
/// <param name="ApproverDeviceRegistrationId">The trusted device whose chat-device-approval-v1 signature authorized this registration.</param>
/// <param name="ApproverSessionId">The approver's own session at the moment it signed - re-validated as still live at commit, not trusted as evidence of anything on its own.</param>
/// <param name="SignatureVerified">Whether the caller already verified that signature against the server-stored challenge binding and the approver's registered key.</param>
public sealed record ApprovalAssertion(string ApproverDeviceRegistrationId, string ApproverSessionId, bool SignatureVerified);
