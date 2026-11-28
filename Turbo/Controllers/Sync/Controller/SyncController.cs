using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Services.Sync.Contracts;
using Services.Sync.Interface;
using Services.Sync.Models;

namespace Turbo.Controllers.Sync.Controller;

/// <summary>
/// Stage 13 HTTP sync (plan/13-history-api.md): "HTTP доставя историята и промените при синхронизация"
/// and "HTTP може да се обслужи от друга реплика чрез общото устойчиво състояние" - every one of these
/// actions works purely from the durable session/boundary/cursor rows, so the replica that answers a
/// page need not be the one that took the handshake.
///
/// Deliberately thin, like every other controller here: it reads the request body and writes the answer
/// in the V1 ENCODING (<see cref="SyncProtocol.CreateJsonOptions"/>) rather than with the MVC defaults,
/// because the contract's camelCase/exact-enum-name shape and the 1 MiB page cap have to hold on the
/// real wire bytes - the same reason the hub exchanges these DTOs as JSON strings. All decisions belong
/// to <see cref="ISyncPageService"/>.
///
/// Plain <c>[Authorize]</c>, i.e. the default app-session policy: an "app"-purpose token whose
/// session/device/account are trusted RIGHT NOW. The page service then revalidates the sync session and
/// the current rights on every page, because a token that is still valid says nothing about whether the
/// fixed view a cursor belongs to is still the current one.
/// </summary>
[ApiController]
[Authorize]
[Route("sync/v1")]
public class SyncController(ISyncPageService pages) : ControllerBase
{
	/// <summary>
	/// A page REQUEST is a handful of identifiers and one opaque cursor; anything larger is refused
	/// before it is parsed. (The 1 MiB cap of the contract bounds the RESPONSE, which the page service
	/// enforces on its own serialized bytes.)
	/// </summary>
	private const int MaxRequestBytes = 16 * 1024;

	private static readonly JsonSerializerOptions SyncJson = SyncProtocol.CreateJsonOptions();

	/// <summary>The account's chats, their memberships, unread and metadata - a full enumeration of the fixed set, or the catalog journal once this device is past its initial restoration.</summary>
	[HttpPost("catalog")]
	public Task<IActionResult> Catalog(CancellationToken ct) => ReadAsync(pages.ReadCatalogAsync, ct);

	/// <summary>One chat scope's permitted messages with their keys and statuses, read from the entity tables - the surface a full restoration and a newly granted old period both use.</summary>
	[HttpPost("history")]
	public Task<IActionResult> History(CancellationToken ct) => ReadAsync(pages.ReadHistoryAsync, ct);

	/// <summary>The changes of one stream since the position this device uploaded.</summary>
	[HttpPost("delta")]
	public Task<IActionResult> Delta(CancellationToken ct) => ReadAsync(pages.ReadDeltaAsync, ct);

	private async Task<IActionResult> ReadAsync(
		Func<ClaimsPrincipal, SyncPageRequest, CancellationToken, Task<SyncResult<SyncPage>>> read, CancellationToken ct)
	{
		if (Request.ContentLength > MaxRequestBytes)
		{
			return Failure(new SyncFailure(SyncErrorCode.InvalidRequest, null), HttpStatusCode.RequestEntityTooLarge);
		}

		string body;
		using (StreamReader reader = new(Request.Body, Encoding.UTF8))
		{
			body = await reader.ReadToEndAsync(ct);
		}

		if (Encoding.UTF8.GetByteCount(body) > MaxRequestBytes)
		{
			// A request without a declared length still cannot be unbounded.
			return Failure(new SyncFailure(SyncErrorCode.InvalidRequest, null), HttpStatusCode.RequestEntityTooLarge);
		}

		SyncPageRequest? request;
		try
		{
			request = JsonSerializer.Deserialize<SyncPageRequest>(body, SyncJson);
		}
		catch (JsonException)
		{
			request = null;
		}

		if (request is null)
		{
			return Failure(new SyncFailure(SyncErrorCode.InvalidRequest, null), HttpStatusCode.BadRequest);
		}

		SyncResult<SyncPage> result = await read(User, request, ct);

		// "Authorization failures are not successful empty pages" - a refusal is a non-success status
		// carrying the contract's own failure code, never a page with nothing in it.
		return result.Failure is SyncFailure failure
			? Failure(failure, StatusOf(failure.Code))
			: Content(JsonSerializer.Serialize(result.Value!, SyncJson), "application/json", Encoding.UTF8);
	}

	private ContentResult Failure(SyncFailure failure, HttpStatusCode status) =>
		new()
		{
			StatusCode = (int)status,
			ContentType = "application/json",
			Content = JsonSerializer.Serialize(failure, SyncJson),
		};

	/// <summary>
	/// One code, one status. CursorInvalid and RightsChanged are 409: the request was well formed and the
	/// caller is authenticated - what changed is the state it was built against, and the client's answer
	/// to both is to reconcile, not to retry the same page.
	/// </summary>
	private static HttpStatusCode StatusOf(SyncErrorCode code) => code switch
	{
		SyncErrorCode.SessionExpired => HttpStatusCode.Unauthorized,
		SyncErrorCode.DeviceRevoked => HttpStatusCode.Forbidden,
		SyncErrorCode.CursorInvalid or SyncErrorCode.RightsChanged => HttpStatusCode.Conflict,
		SyncErrorCode.Unavailable => HttpStatusCode.ServiceUnavailable,
		_ => HttpStatusCode.BadRequest,
	};
}
