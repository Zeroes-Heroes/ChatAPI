using System.ComponentModel.DataAnnotations;
using Phone = ChatAPI.Domain.Validations.Attributes.PhoneAttribute;

namespace ChatAPI.Application.DTOs.Authorization
{
    public class UserRegisterDTO
    {
        [Required, StringLength(50), @Phone]
        public required string Phone { get; set; }

        [Required, StringLength(100)]
        public required string Name { get; set; }

        [Required, StringLength(100)]
        public required string DeviceId { get; set; }
    }
}
