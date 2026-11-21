using System.Security.Claims;
using Services.Authorship.Models;
using Services.Sync.Contracts;
using Services.Utilities;

namespace Services.Authorship.Interface;

/// <summary>
/// The server-authorized author-key registry (plan/11-message-acceptance.md: "SignalR приема подписания
/// криптиран пакет след проверка на устройството, автора и правата"). It answers exactly one question -
/// which public key a complete <see cref="AuthorKeyReference"/> is bound to - and owns the one-time
/// binding of it, per docs/sync-contract-v1.md: "The key comes from the server-authorized author-key
/// registry over authenticated TLS, never from an untrusted JWS header or embedded client-supplied key.
/// Once a reference is bound to SPKI, a different SPKI for that same reference is a conflict; rotation
/// uses a new version."
///
/// Registration is the only path by which a key enters the registry, and it is deliberately NOT part of
/// accepting a message: a package may never carry the key it is verified against. The registering caller
/// must be a currently trusted device whose own account context equals the reference's account (taken
/// from session claims, never from the payload) and which currently has an active stint in the
/// reference's chat - a frozen archive publishes no new authorship.
/// </summary>
public interface IAuthorKeyRegistry
{
	/// <summary>
	/// Binds <paramref name="spki"/> to <paramref name="reference"/>, or confirms the existing identical
	/// binding. Runs inside its own <see cref="Services.Sessions.Service.TrustedMutation"/>, so a device
	/// revoked while the call is in flight cannot publish an author context.
	/// </summary>
	Task<Result<AuthorKeyRegistration>> RegisterAsync(ClaimsPrincipal caller, AuthorKeyReference reference, byte[] spki, CancellationToken ct = default);

	/// <summary>
	/// The DER SubjectPublicKeyInfo bound to this exact reference, or null when nothing is bound. Takes
	/// no authorization decision of its own: the caller has already decided whose package this is
	/// supposed to be, and a reference that does not resolve must fail the package, never fall back to
	/// anything the package itself supplied.
	/// </summary>
	Task<byte[]?> FindSpkiAsync(AuthorKeyReference reference, CancellationToken ct = default);
}
