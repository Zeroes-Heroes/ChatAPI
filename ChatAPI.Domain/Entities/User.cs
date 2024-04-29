using System.ComponentModel.DataAnnotations;

namespace ChatAPI.Domain.Entities
{
	public class User
	{
		public User(string name, string phone)
		{
			Name = name;
			Phone = phone;
			CreatedOn = DateTime.UtcNow;
		}

		public int Id { get; set; }

		[Required]
		public required string Name { get; set; }

		[Required]
		public required string Phone { get; set; }

		[Required]
		public DateTime CreatedOn { get; set; }

		public virtual ICollection<UserDevice> UserDevices { get; set; }
    };
}