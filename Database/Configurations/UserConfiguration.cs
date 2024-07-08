using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations;

internal class UserConfiguration : IEntityTypeConfiguration<UserEntity>
{
	public void Configure(EntityTypeBuilder<UserEntity> builder)
	{
		builder.ToTable("Users");

		builder.Property(e => e.Id).UseIdentityAlwaysColumn();

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Phone)
			.IsRequired()
			.HasMaxLength(50);

		builder.Property(e => e.Name)
			.IsRequired()
			.HasMaxLength(100);

		builder.Property(e => e.CreatedOn)
			.IsRequired();
	}
}