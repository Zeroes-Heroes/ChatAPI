using System.ComponentModel.DataAnnotations;

namespace Domain.Entities
{
	public class User
	{
		public int Id { get; set; }

		[Required]
		public required string Email { get; set; }

		[Required]
		public required string Password { get; set; }

		[Required]
		public required string Phone { get; set; }

		[Required]
		public DateTime CreatedOn { get; set; }
	};
}