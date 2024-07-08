using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations;

internal class UserLoginCodeConfiguration : IEntityTypeConfiguration<UserLoginCodeEntity>
{
	public void Configure(EntityTypeBuilder<UserLoginCodeEntity> builder)
	{
		builder.ToTable("UserLoginCodes");

		builder.HasKey(e => e.UserDeviceId);

		builder.Property(e => e.SecretLoginCode)
			.IsRequired()
			.HasMaxLength(36);

		builder
			.HasOne(e => e.UserDevice)
			.WithOne(e => e.UserLoginCode)
			.HasForeignKey<UserLoginCodeEntity>(e => e.UserDeviceId)
			.HasPrincipalKey<UserDeviceEntity>(e => e.Id)
			.HasConstraintName("FK_UserLoginCodes_UserDeviceId_UserDevices_Id");
	}
}