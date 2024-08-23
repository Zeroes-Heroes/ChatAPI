using System.ComponentModel.DataAnnotations;

namespace Database.Entities
{
	public class ChatEntity
	{
		public ChatEntity() { }

		public ChatEntity(string? name, ICollection<UserEntity> users)
		{
			Name = name;
			Users = users;
		}

		[Key]
		public int Id { get; set; }

		public string? Name { get; set; }

		public ICollection<UserEntity> Users { get; set; } = [];
	}
}
