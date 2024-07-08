using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations;

internal class UserDeviceConfiguration : IEntityTypeConfiguration<UserDeviceEntity>
{
	public void Configure(EntityTypeBuilder<UserDeviceEntity> builder)
	{
		builder.ToTable("UserDevices");

		builder.Property(e => e.Id).UseIdentityAlwaysColumn();

		builder.HasKey(e => e.Id);

		builder.Property(e => e.DeviceId)
			.IsRequired()
			.HasMaxLength(100);

		builder.Property(e => e.CreatedOn)
			.IsRequired();

		builder
			.HasOne(e => e.User)
			.WithMany(e => e.UserDevices)
			.HasForeignKey(e => e.UserId)
			.HasPrincipalKey(e => e.Id)
			.HasConstraintName("FK_UserDevices_UserId_Users_Id");
	}
}