using ChatAPI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatAPI.Persistence.Configurations
{
	internal class UserLoginCodeConfiguration : IEntityTypeConfiguration<UserLoginCode>
	{
		public void Configure(EntityTypeBuilder<UserLoginCode> builder)
		{
			builder.ToTable("UserLoginCodes");

			builder.HasKey(e => e.UserId);

			builder.Property(e => e.SecretLoginCode)
				.IsRequired()
				.HasMaxLength(36);

			builder
				.HasOne(e => e.User)
				.WithOne(e => e.UserLoginCode)
				.HasForeignKey<UserLoginCode>(e => e.UserId)
				.HasPrincipalKey<User>(e => e.Id)
				.HasConstraintName("FK_UserLoginCodes_UserId_Users_Id");
		}
	}
}