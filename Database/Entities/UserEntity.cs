using System.ComponentModel.DataAnnotations;

namespace Database.Entities;

public class UserEntity(string name, string phone)
{
	public int Id { get; set; }

	[Required]
	public string Name { get; set; } = name;

	[Required]
	public string Phone { get; set; } = phone;

	[Required]
	public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

	public IEnumerable<FriendshipEntity> Friendships
	{
		get
		{
			List<FriendshipEntity> friendships = [];

			if (SentFriendships is not null)
				friendships.AddRange(SentFriendships);

			if (ReceivedFriendships is not null)
				friendships.AddRange(ReceivedFriendships);

			return friendships;
		}
	}

	public virtual ICollection<UserDeviceEntity>? UserDevices { get; set; } = [];
	public virtual ICollection<FriendshipEntity>? SentFriendships { get; set; } = [];
	public virtual ICollection<FriendshipEntity>? ReceivedFriendships { get; set; } = [];
};