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

		public virtual UserLoginCode? UserLoginCode { get; set; }
		public virtual ICollection<UserDevice> UserDevices { get; set; }
    };
}