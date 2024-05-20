using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.DTOs.Friends
{
	public record FriendDTO(int UserId, string Name, string Phone, FriendshipStatus FriendshipStatus, bool IsInitiator);
}
