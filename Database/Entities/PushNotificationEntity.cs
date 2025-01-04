using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Database.Enums;

namespace Database.Entities
{
    public class PushNotificationEntity
    {
        public PushNotificationEntity(OperatingSystemType operatingSystem, string token, bool IsNotificationEnabled, int UserId, int deviceId)
        {
            OperatingSystem = operatingSystem;
            Token = token;
            this.IsNotificationEnabled = IsNotificationEnabled;
            this.UserId = UserId;
            this.DeviceId = deviceId;
        }

        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [Required]
        public int DeviceId { get; set; }

        [Required]
        public OperatingSystemType OperatingSystem { get; set; }

        [Required]
        public string Token { get; set; }

        public bool IsNotificationEnabled { get; set; }

        [ForeignKey(nameof(UserId))]
        public virtual UserEntity User { get; set; }

        [ForeignKey(nameof(DeviceId))]
        public virtual UserDeviceEntity UserDevice { get; set; } = null!;
    }
}