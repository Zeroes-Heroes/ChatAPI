namespace Services.Friendship.Models;

public record FriendshipDTO(int UserId, string Name, string Phone, int FriendshipStatus, bool IsInitiator);
