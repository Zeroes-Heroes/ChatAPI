using System.ComponentModel.DataAnnotations;

namespace Database.Entities
{
    public class OperationSystemEntity
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string TypeOS { get; set; }

        [Required]
        public virtual ICollection<PushNotificationEntity> PushNotification { get; set; } = [];
    }
}