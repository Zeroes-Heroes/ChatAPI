using System.Net;

namespace Services.Membership;

/// <summary>
/// A membership rule refused this attempt. Thrown rather than returned from the inner, transactional
/// part of a mutation on purpose: escaping <see cref="Services.Sessions.Service.TrustedMutation"/>'s
/// delegate rolls the whole transaction back, so a refused attempt cannot leave half of a projection
/// or a journal entry behind. The service's public methods translate it into
/// <see cref="Services.Utilities.Result"/> with exactly this status.
/// </summary>
public class ChatMembershipRefusedException(string message, HttpStatusCode status) : Exception(message)
{
	public HttpStatusCode Status { get; } = status;
}

/// <summary>A chat with exactly this active participant set already exists, whatever it is named.</summary>
public sealed class DuplicateChatMemberSetException(string message)
	: ChatMembershipRefusedException(message, HttpStatusCode.Conflict);

/// <summary>The caller may not perform this membership change: no active stint of its own, or the participant it is adding has blocked it.</summary>
public sealed class ChatMembershipForbiddenException(string message)
	: ChatMembershipRefusedException(message, HttpStatusCode.Forbidden);
