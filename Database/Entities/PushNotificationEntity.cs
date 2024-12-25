using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Database.Migrations;

namespace Database.Entities
{
    public class PushNotificationEntity
    {
        public PushNotificationEntity(string oS, string token, bool IsTurnOnNotification, int UserId, int deviceId)
        {
            OS = oS;
            Token = token;
            this.IsTurnOnNotification = IsTurnOnNotification;
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
        public string OS { get; set; }

        [Required]
        public string Token { get; set; }

        public bool IsTurnOnNotification { get; set; }

        [ForeignKey(nameof(UserId))]
        public virtual UserEntity? User { get; set; }

        [ForeignKey(nameof(DeviceId))]
        public virtual UserDeviceEntity? UserDevice { get; set; }
    }
}