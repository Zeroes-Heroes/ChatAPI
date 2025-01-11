using System.ComponentModel.DataAnnotations;

namespace Database.Entities;

public class UserEntity
{
	public UserEntity()
	{

	}

	public UserEntity(string name, string phone)
	{
		Name = name;
		Phone = phone;
	}

	public int Id { get; set; }

	[Required]
	public string Name { get; set; }

	[Required]
	public string Phone { get; set; }

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

	public virtual ICollection<UserDeviceEntity> UserDevices { get; set; } = [];
	public virtual ICollection<DeviceNotificationConfigEntity> DeviceNotificationsConfig { get; set; } = [];
	public virtual ICollection<FriendshipEntity> SentFriendships { get; set; } = [];
	public virtual ICollection<FriendshipEntity> ReceivedFriendships { get; set; } = [];
	public virtual ICollection<ChatEntity> Chats { get; set; } = [];
	public ICollection<MessageStatusEntity> MessageStatusEntities { get; set; } = [];
};