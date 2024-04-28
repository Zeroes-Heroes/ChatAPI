using ChatAPI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatAPI.Persistence.Configurations
{
	internal class UserConfiguration : IEntityTypeConfiguration<User>
	{
		public void Configure(EntityTypeBuilder<User> builder)
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
}