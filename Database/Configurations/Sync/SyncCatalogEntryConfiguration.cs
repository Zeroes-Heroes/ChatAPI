using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncCatalogEntryConfiguration : IEntityTypeConfiguration<SyncCatalogEntryEntity>
{
	public void Configure(EntityTypeBuilder<SyncCatalogEntryEntity> builder)
	{
		builder.ToTable("SyncCatalogEntries", tb =>
			tb.HasCheckConstraint("CK_SyncCatalogEntries_CatalogSequence_Positive", "\"CatalogSequence\" > 0"));

		builder.HasKey(e => new { e.AccountId, e.CatalogSequence });

		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.ReferenceId).HasMaxLength(100);

		builder.Property(e => e.Kind).IsRequired();
		builder.Property(e => e.OccurredAtUtc).IsRequired();

		builder.HasIndex(e => new { e.AccountId, e.ChatId });

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncCatalogEntries_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(e => e.Chat)
			.WithMany()
			.HasForeignKey(e => e.ChatId)
			.HasConstraintName("FK_SyncCatalogEntries_ChatId_SyncChats_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
