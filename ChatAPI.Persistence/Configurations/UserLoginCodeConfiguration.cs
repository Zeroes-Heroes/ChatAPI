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

			builder.HasKey(e => e.UserDeviceId);

			builder.Property(e => e.SecretLoginCode)
				.IsRequired()
				.HasMaxLength(36);

			builder
				.HasOne(e => e.UserDevice)
				.WithOne(e => e.UserLoginCode)
				.HasForeignKey<UserLoginCode>(e => e.UserDeviceId)
				.HasPrincipalKey<UserDevice>(e => e.Id)
				.HasConstraintName("FK_UserLoginCodes_UserDeviceId_UserDevices_Id");
		}
	}
}