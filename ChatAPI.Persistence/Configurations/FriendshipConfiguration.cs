using ChatAPI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatAPI.Persistence.Configurations
{
	internal class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
	{
		public void Configure(EntityTypeBuilder<Friendship> builder)
		{
			builder.ToTable("Friendships");

			builder.Property(e => e.Id).UseIdentityAlwaysColumn();
			builder.HasKey(e => e.Id);

			builder.Property(e => e.SenderUserId)
				.IsRequired();

			builder.Property(e => e.TargetUserId)
				.IsRequired();

			builder.Property(e => e.CreatedOn)
				.IsRequired();

			builder.Property(e => e.Status)
				.IsRequired();

			builder
				.HasOne(e => e.Sender)
				.WithMany(e => e.SentFriendships)
				.HasForeignKey(e => e.SenderUserId)
				.HasPrincipalKey(e => e.Id)
				.HasConstraintName("FK_Friendships_SenderUserId_Users_Id");

			builder
				.HasOne(e => e.Target)
				.WithMany(e => e.ReceivedFriendships)
				.HasForeignKey(e => e.TargetUserId)
				.HasPrincipalKey(e => e.Id)
				.HasConstraintName("FK_Friendships_TargetUserId_Users_Id");
		}
	}
}