using System.Security.Claims;
using Services.Authorship.Interface;
using Services.Authorship.Models;
using Services.Extensions;
using Services.Membership.Models;
using Services.Sessions;

namespace Services.Authorship.Service;

/// <summary>See <see cref="IMessageAuthorshipAuthority"/> for the three conditions and why they are these three.</summary>
public sealed class MessageAuthorshipAuthority : IMessageAuthorshipAuthority
{
	public IReadOnlySet<int> RevisableByAuthor(
		ClaimsPrincipal caller, ChatAccess access, IReadOnlyCollection<AuthoredMessage> messages)
	{
		HashSet<int> revisable = [];
		if (messages.Count == 0)
		{
			return revisable;
		}

		// Fail closed on both identity claims. Every app-session token carries them (the stage-05
		// AppSessionRequirement refuses a token without them before any surface is reached), so a missing
		// one means this was reached by something that is not an authorized app session - which must hold
		// no author rights at all, rather than fall back to the legacy user id.
		string? accountContextId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		string? deviceRegistrationId = caller.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);
		if (string.IsNullOrEmpty(accountContextId) || string.IsNullOrEmpty(deviceRegistrationId))
		{
			return revisable;
		}

		int authorLegacyUserId = caller.Id();

		foreach (AuthoredMessage message in messages)
		{
			// Own message, and still inside a window this caller holds - INCLUDING a frozen one, which is
			// the case the plan names explicitly ("собствено съобщение в архив от разрешено устройство").
			if (message.SenderLegacyUserId == authorLegacyUserId && access.PermitsMessageAt(message.CreatedAtUtc))
			{
				revisable.Add(message.MessageId);
			}
		}

		return revisable;
	}
}
