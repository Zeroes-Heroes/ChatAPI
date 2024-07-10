using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations;

internal class CountryCodesConfiguration : IEntityTypeConfiguration<CountryCodesEntity>
{
	public void Configure(EntityTypeBuilder<CountryCodesEntity> builder)
	{
		builder.ToTable("CountryCodes");

		builder.HasIndex(c => c.Code);
	}
}
