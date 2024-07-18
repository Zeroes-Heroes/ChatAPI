using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations;

internal class ChatEntityConfiguration : IEntityTypeConfiguration<ChatEntity>
{
	public void Configure(EntityTypeBuilder<ChatEntity> builder)
	{
		builder.ToTable("Chats");

		builder
			.HasMany(c => c.Users)
			.WithMany(u => u.Chats)
			.UsingEntity<Dictionary<string, object>>(
					"ChatsUsers",
					j => j
						.HasOne<UserEntity>()
						.WithMany()
						.HasForeignKey("UserId")
						.HasConstraintName("FK_ChatsUsers_Users_UserId")
						.OnDelete(DeleteBehavior.Cascade),
					j => j
						.HasOne<ChatEntity>()
						.WithMany()
						.HasForeignKey("ChatId")
						.HasConstraintName("FK_ChatsUsers_Chats_ChatId")
						.OnDelete(DeleteBehavior.Cascade),
					j =>
					{
						j.HasKey("ChatId", "UserId");
						j.ToTable("ChatsUsers");
					});
	}
}
