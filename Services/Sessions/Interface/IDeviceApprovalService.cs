using Database.Context;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Interface;

/// <summary>
/// The trusted device's side of plan/06-recovery.md's approval flow: see the exact pending
/// registrations for this account, and record an approval signature for exactly one of them. Holds no
/// trust rules of its own - the decision that an approval is required, and the commit-time re-check of
/// the approver, stay in <see cref="IDeviceTrustService"/>; this service
/// only carries the approver's signature from one device to the registration commit, bound to the one
/// challenge row it was produced for.
///
/// Every account/device/session identifier here comes from the approver's own authenticated session
/// claims. The request body contributes only the challenge id and the signature - deliberately not the
/// approving session (docs/sync-contract-v1.md: "supplied ApprovingSession is not authority").
/// </summary>
public interface IDeviceApprovalService
{
	/// <summary>Pending (unconsumed, unexpired) approval-purpose requests for this account context, with every field the pinned chat-device-approval-v1 array is built from.</summary>
	Task<IReadOnlyList<WireContracts.PendingApprovalRequest>> ListPendingAsync(AppDbContext db, string accountId, DateTime now, CancellationToken ct = default);

	/// <summary>
	/// Verifies the approver's signature against the server-stored challenge binding and the approver's
	/// own currently-registered key, then records the grant. Idempotent for the same (challenge,
	/// approver) pair - a repeated tap returns <see cref="WireContracts.DeviceApprovalOutcome.AlreadyApproved"/>
	/// instead of failing or creating a second grant.
	/// </summary>
	/// <exception cref="ApprovalRequestInvalidException">Unknown/expired/already-consumed request, a request belonging to another account, a self-approval, or an already-granted request from a different approver.</exception>
	/// <exception cref="DeviceProofInvalidException">The approval signature does not verify against the exact pending request.</exception>
	/// <exception cref="SessionNotTrustedException">The approving device/session is not currently trusted at the moment the grant is recorded.</exception>
	Task<WireContracts.ApproveDeviceResult> ApproveAsync(
		AppDbContext db, string accountId, string approvingDeviceRegistrationId, string approvingSessionId,
		WireContracts.ApproveDeviceRequest request, DateTime now, CancellationToken ct = default);

	/// <summary>
	/// The author-key handoff an approver sealed for THIS device, so approval alone restores the original
	/// authorship identity. Deliberately NOT consumed by reading: a lost response or a crash before the
	/// keys are imported locally must not destroy the only copy. Returns null when nothing is pending.
	/// </summary>
	Task<WireContracts.AuthorKeyHandoffView?> GetPendingHandoffAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, CancellationToken ct = default);

	/// <summary>Marks a handoff delivered once the device has durably imported it. Idempotent, and only ever for the device it was sealed for.</summary>
	Task AcknowledgeHandoffAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string challengeId, DateTime now, CancellationToken ct = default);
}
