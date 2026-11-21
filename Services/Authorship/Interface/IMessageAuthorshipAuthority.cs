using System.Security.Claims;
using Services.Authorship.Models;
using Services.Membership.Models;

namespace Services.Authorship.Interface;

/// <summary>
/// The author rights a future edit/delete will need (plan/09-contacts-privacy.md: "Подготвят се
/// авторските права за бъдещи edit/delete, включително собствено съобщение в архив от разрешено
/// устройство. Самите операции остават бъдещ етап"; MESSAGE_SYNC_ANALYSIS.md: "Подготвяните права за
/// бъдещи edit/delete принадлежат на автора от всяко негово разрешено устройство, включително за
/// собствените съобщения в архив само за четене. Устройството трябва да има действащо доверие и достъп
/// до съобщението").
///
/// Three conditions, all of which must hold:
/// <list type="number">
/// <item><b>Authorship</b> - the message was written by this caller's own account context. A phone that
/// started over is a different context, and the chats of the context it replaced are not reachable at
/// all (<see cref="Services.Sessions.Interface.IAccountContextBoundary"/>, composed into the access
/// answer below), so it inherits no author rights over them.</item>
/// <item><b>Access to the message</b> - <see cref="ChatAccess.PermitsMessageAt"/>. A FROZEN window
/// counts: an author keeps the right over its own message inside a read-only archive, which is the one
/// case the plan calls out by name.</item>
/// <item><b>A currently trusted device</b> - every caller that reaches this has passed the stage-05
/// <c>AppSessionRequirement</c>, which re-checks <see cref="Services.Sessions.Interface.ISessionTrustChecker"/>
/// per request, and must still carry its device-registration claim here.</item>
/// </list>
///
/// This stage answers the question and publishes the answer (<c>GET /chat/history</c>); it performs no
/// mutation. When the edit/delete operations arrive, they must ask THIS and then re-validate trust at
/// the moment of the write through <see cref="Services.Sessions.Service.TrustedMutation"/>, exactly as
/// every other mutation in this codebase does - a read-time answer is not a write-time authorization.
/// </summary>
public interface IMessageAuthorshipAuthority
{
	/// <summary>
	/// Of <paramref name="messages"/> (already authorized for reading by <paramref name="access"/>), the
	/// ids this caller holds author rights over.
	/// </summary>
	IReadOnlySet<int> RevisableByAuthor(ClaimsPrincipal caller, ChatAccess access, IReadOnlyCollection<AuthoredMessage> messages);
}
