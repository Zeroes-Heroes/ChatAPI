using Application.Utilities;

namespace Application.UseCases.Abstractions
{
	public interface IFriendshipService
	{
		Task<Result> AddFriend(int senderUserId, string targetEmail);
	}
}
