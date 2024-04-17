using Domain.Enums;

namespace Application.DTOs.Friends
{
	public record FriendDTO(string Email, string Phone, FriendshipStatus FriendshipStatus);
}
