using System.Security.Claims;
using Database.Enums;
using Services.Friendship.Models;
using Services.Utilities;

namespace Services.Friendship.Interface;

public interface IFriendshipService
{
	/// <param name="actingUser">The caller's own authenticated principal - re-validated as currently trusted immediately before the friendship row actually commits and the other party is notified, not merely at the controller's own authorization-on-entry (see Services.Sessions.Service.TrustedMutation's own doc comment).</param>
	Task<Result<FriendshipDTO>> AddFriend(int senderUserId, string phone, ClaimsPrincipal actingUser);
	Task<IEnumerable<FriendshipDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null);
	/// <param name="actingUser">See <see cref="AddFriend"/>'s own doc comment - same treatment for the status-change commit/notification below.</param>
	Task<Result> RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus, ClaimsPrincipal actingUser);
}
