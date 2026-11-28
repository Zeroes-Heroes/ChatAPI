using Services.Sessions.Models;

namespace Services.Sessions.Interface;

/// <summary>
/// AES-256-GCM protection of the refresh-rotation replay cache (the new tokens' JSON, kept only long
/// enough for a bounded-window idempotent retry - see <see cref="Database.Entities.Sync.SyncRefreshTokenRotationEntity"/>'s
/// doc comment). Built the same way as <see cref="Services.Keys.Interface.IMessageKeyProtector"/>
/// (same <see cref="Services.Keys.Interface.IMasterSecretProvider"/>, same fresh-nonce-per-call,
/// versioned-AAD shape) but deliberately a separate interface: that one's shape is fixed to wrapping a
/// single raw key (<c>ReadOnlySpan&lt;byte&gt; rawKey</c>) with a <c>KeyProtectionContext</c> whose
/// fields (FormatVersion/Purpose/OwnerContextId/KeyId) are message-key-shaped; forcing an arbitrary-
/// length JSON payload through that exact interface would mean a session/operation id doing duty as a
/// "KeyId" it is not. This is the sibling primitive for a variable-length payload with its own
/// (account, session, operation)-shaped context.
/// </summary>
public interface IReplayPayloadProtector
{
	ProtectedReplayPayload Protect(ReadOnlySpan<byte> plaintext, ReplayProtectionContext context);

	/// <exception cref="Services.Keys.MasterSecretUnavailableException"><paramref name="payload"/>'s master version is not available.</exception>
	/// <exception cref="Services.Keys.KeyProtectionIntegrityException">Authentication failed - tampered bytes or a mismatched <paramref name="context"/>.</exception>
	byte[] Unprotect(ProtectedReplayPayload payload, ReplayProtectionContext context);
}
