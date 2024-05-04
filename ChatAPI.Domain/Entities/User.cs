using System.ComponentModel.DataAnnotations;

namespace ChatAPI.Domain.Entities
{
	public class User(string name, string phone)
	{
		public int Id { get; set; }

		[Required]
		public string Name { get; set; } = name;

		[Required]
		public string Phone { get; set; } = phone;

		[Required]
		public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
		public IEnumerable<Friendship> Friendships
		{
			get
			{
				List<Friendship> friendships = [];
				
				if (SentFriendships is not null)
					friendships.AddRange(SentFriendships);

				if (ReceivedFriendships is not null)
					friendships.AddRange(ReceivedFriendships);

				return friendships;
			}
		}

		public virtual ICollection<UserDevice>? UserDevices { get; set; }
		public virtual ICollection<Friendship>? SentFriendships { get; set; }
		public virtual ICollection<Friendship>? ReceivedFriendships { get; set; }
	};
}