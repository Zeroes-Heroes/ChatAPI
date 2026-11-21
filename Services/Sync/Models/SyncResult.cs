using Services.Sync.Contracts;

namespace Services.Sync.Models;

/// <summary>
/// Either the v1 answer or the v1 <see cref="SyncFailure"/> - never a successful empty page standing in
/// for a refusal (docs/sync-contract-v1.md: "Authorization failures are not successful empty pages").
/// The transport maps the code to its own status; the code itself is what the client acts on.
/// </summary>
public sealed record SyncResult<T>(T? Value, SyncFailure? Failure)
	where T : class
{
	public bool IsSuccess => Failure is null;

	public static SyncResult<T> Ok(T value) => new(value, null);

	public static SyncResult<T> Fail(SyncErrorCode code, int? retryAfterMs = null) => new(null, new SyncFailure(code, retryAfterMs));
}
