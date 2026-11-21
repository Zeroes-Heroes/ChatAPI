using Database.Entities;
using Database.Enums;
using Services.Friendship.Models;
using Services.Repositories.Base.Interface;

namespace Services.Repositories.Friendship.Interface;

public interface IFriendshipRepository : IBaseRepository
{
	void AddFriendship(int senderUserId, int targetUserId);
	Task<FriendshipEntity?> GetSpecificFriendshipByUserIds(int senderUserId, int targetUserId);
	Task<FriendshipEntity?> GetAnyFriendshipByUserIds(params int[] userIds);
	/// <summary>
	/// Every legacy relation row of this user, unfiltered and unprojected. Stage 09 moved the status/role
	/// filtering out of the SQL on purpose: a block overlays both the status and the initiator flag
	/// (see <see cref="Services.Contacts.Service.PersonalRelationProjection"/>), so filtering here would
	/// answer about the row rather than about what the caller is shown.
	/// </summary>
	Task<IReadOnlyList<FriendshipRelationRow>> GetUserRelations(int userId);
	Task<bool> AreUsersFriends(int[] userIds);
}
