using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMessageKeyConfiguration : IEntityTypeConfiguration<SyncMessageKeyEntity>
{
	public void Configure(EntityTypeBuilder<SyncMessageKeyEntity> builder)
	{
		builder.ToTable("SyncMessageKeys");

		builder.HasKey(e => e.MessageId);
		builder.Property(e => e.MessageId).HasMaxLength(100);
		builder.Property(e => e.KeyVersion).HasMaxLength(50).IsRequired();

		builder.Property(e => e.ProtectedKey).IsRequired();
		builder.Property(e => e.Nonce).IsRequired();
		builder.Property(e => e.Tag).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		builder
			.HasOne(e => e.Message)
			.WithOne(e => e.Key)
			.HasForeignKey<SyncMessageKeyEntity>(e => e.MessageId)
			.HasConstraintName("FK_SyncMessageKeys_MessageId_SyncMessages_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
