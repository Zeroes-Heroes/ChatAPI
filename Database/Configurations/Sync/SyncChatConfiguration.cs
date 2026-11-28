using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncChatConfiguration : IEntityTypeConfiguration<SyncChatEntity>
{
	public void Configure(EntityTypeBuilder<SyncChatEntity> builder)
	{
		builder.ToTable("SyncChats");

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);

		builder.Property(e => e.Kind).IsRequired();
		builder.Property(e => e.Name).HasMaxLength(200);
		builder.Property(e => e.CreatedAtUtc).IsRequired();
	}
}
