using System.ComponentModel.DataAnnotations;

namespace Database.Entities
{
    public class OperationSystemEntity
    {
        public OperationSystemEntity(string typeOS)
        {
            TypeOS = typeOS;
        }

        [Key]
        public int Id { get; set; }

        [Required]
        public string TypeOS { get; set; }

        [Required]
        public virtual ICollection<PushNotificationEntity> PushNotifications { get; set; } = [];
    }
}