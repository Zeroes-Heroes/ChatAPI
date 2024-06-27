using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.DTOs.Friends
{
	public record FriendshipDTO(int UserId, string Name, string Phone, int FriendshipStatus, bool IsInitiator);
}
