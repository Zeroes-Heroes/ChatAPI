using System.ComponentModel.DataAnnotations;
using ChatAPI.Domain.Validations.Attributes;
using Phone = ChatAPI.Domain.Validations.Attributes.PhoneAttribute;

namespace ChatAPI.Application.DTOs.Authorization
{
    public class VerifySmsCodeDTO
    {
        [Required, StringLength(50), @Phone]
        public required string Phone { get; set; }

        [Required, StringLength(100)]
        public required string DeviceId { get; set; }

        [Required, StringLength(maximumLength: 6, MinimumLength = 6), NumbersOnly]
        public required string SmsVerificationCode { get; set; }
    }
}
