using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations;

public class MessageStatusEntityConfiguration : IEntityTypeConfiguration<MessageStatusEntity>
{
	public void Configure(EntityTypeBuilder<MessageStatusEntity> builder)
	{
		builder.HasKey(m => new { m.MessageId, m.ReceiverId, m.Status });

		builder
		   .Property(u => u.Status)
		   .HasConversion<int>();
	}
}
