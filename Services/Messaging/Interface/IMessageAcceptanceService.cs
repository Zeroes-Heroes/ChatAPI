using System.Security.Claims;
using Services.Messaging.Models;
using Services.Sync.Contracts;
using Services.Utilities;

namespace Services.Messaging.Interface;

/// <summary>
/// The v1 <c>SendText</c> acceptance (plan/11-message-acceptance.md): "SignalR приема подписания
/// криптиран пакет след проверка на устройството, автора и правата. Една PostgreSQL транзакция записва
/// съобщението, защитения ключ, първоначалния резултат, OwnSend промените, журнала и outbox. Server ACK
/// се изпраща след commit."
///
/// One transaction, in this order: <see cref="Services.Sessions.Service.TrustedMutation"/> (account →
/// device/session) → the chat's own counter row
/// (<see cref="Services.Membership.Interface.IChatAccessAuthority.LockChatAndAuthorizeAsync"/>, which is
/// what serializes acceptance with removal/leave/blocking) → a transaction-scoped advisory lock on the
/// operation identity → the result lookup → verification → the writes. Nothing takes these locks in any
/// other order.
///
/// The operation identity is the whole tuple (account context, device registration, local instance,
/// clientOperationId), and it is the primary key of <c>SyncOperationResults</c>. It is read INSIDE the
/// transaction and BEFORE any rights check, which is what makes "Удостоверена собствена приета операция
/// се разпознава и след промяна на членството" true: an already accepted operation answers with its
/// original result - same MessageId, same time, same sequences, same OwnSend boundary - whatever has
/// happened to the membership since.
/// </summary>
public interface IMessageAcceptanceService
{
	/// <summary>
	/// Accepts (or re-answers) one signed package. The failure side of the <see cref="Result{T}"/> means
	/// no operation could even be NAMED - a package that does not parse - which the caller reports as a
	/// transport-level failure rather than as an outcome of some operation; every other refusal is a
	/// well-formed <see cref="OperationResult"/> on the success side.
	/// </summary>
	Task<Result<AcceptanceOutcome>> AcceptAsync(ClaimsPrincipal caller, SendMessageRequest request, CancellationToken ct = default);

	/// <summary>
	/// The committed outcome of one of the CALLER'S OWN operations, or <see cref="UnknownOperation"/>
	/// when none is committed at this lookup's serialization point ("Unknown means no committed result at
	/// lookup's serialization point; an in-flight attempt may still commit"). Authenticates the session
	/// and checks that the queried identity is this device's own, and then consults the result -
	/// deliberately WITHOUT any membership check, per the contract's "After authenticating active
	/// session/device, consult the own operation result before checking current membership."
	/// </summary>
	Task<Result<AcceptanceOutcome>> QueryAsync(ClaimsPrincipal caller, OperationQuery query, CancellationToken ct = default);
}
