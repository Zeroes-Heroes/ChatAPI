using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ChatAPI.Domain.Entities
{
	public class UserLoginCode
	{
        public UserLoginCode(int userId, Guid secretLoginCode)
        {
            UserId = userId;
			SecretLoginCode = secretLoginCode;
        }

        [Key]
		public int UserId { get; set; }
		public Guid SecretLoginCode { get; set; }
		
		[ForeignKey(nameof(UserId))]
		public virtual User? User { get; set; }
	}
}
