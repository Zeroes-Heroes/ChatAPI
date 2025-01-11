using Database.Enums;

namespace Database.Entities
{
    public class DeviceNotificationConfigEntity
    {
        public DeviceNotificationConfigEntity(OperatingSystemType operatingSystem, string token, bool IsNotificationEnabled, int UserId, int deviceId)
        {
            OperatingSystem = operatingSystem;
            Token = token;
            this.IsNotificationEnabled = IsNotificationEnabled;
            this.UserId = UserId;
            this.DeviceId = deviceId;
        }

        public int Id { get; set; }

        public int UserId { get; set; }

        public int DeviceId { get; set; }

        public OperatingSystemType OperatingSystem { get; set; }

        public string Token { get; set; }

        public bool IsNotificationEnabled { get; set; }

        public virtual UserEntity User { get; set; }

        public virtual UserDeviceEntity UserDevice { get; set; } = null!;
    }
}