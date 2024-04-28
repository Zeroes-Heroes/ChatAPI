using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.DTOs.Friends
{
	public record FriendDTO(int FriendUserId, string Email, string Phone, FriendshipStatus FriendshipStatus, bool IsInitiator);
}
