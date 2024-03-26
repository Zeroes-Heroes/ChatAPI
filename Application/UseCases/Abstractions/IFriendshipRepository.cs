namespace Application.UseCases.Abstractions
{
	public interface IFriendshipRepository
	{
		Task AddFriendship(int senderUserId, int targetUserId);
	}
}
