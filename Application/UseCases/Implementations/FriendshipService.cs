using Application.UseCases.Abstractions;
using Application.Utilities;
using Domain.Entities;

namespace Application.UseCases.Implementations
{
	public class FriendshipService(IUserRepository userRepo, IFriendshipRepository friendRepo) : IFriendshipService
	{
		public async Task<Result> AddFriend(int senderUserId, string targetEmail)
		{
			User? targetUser = await userRepo.GetUserByEmail(targetEmail, trackEntity: false);

			if (targetUser is null)
				return Result.Failure("User with that email is not found.");

			await friendRepo.AddFriendship(senderUserId, targetUser.Id);
			return Result.Success();
		}
	}
}
