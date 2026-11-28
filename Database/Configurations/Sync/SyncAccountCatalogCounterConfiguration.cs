using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncAccountCatalogCounterConfiguration : IEntityTypeConfiguration<SyncAccountCatalogCounterEntity>
{
	public void Configure(EntityTypeBuilder<SyncAccountCatalogCounterEntity> builder)
	{
		builder.ToTable("SyncAccountCatalogCounters", tb =>
			tb.HasCheckConstraint("CK_SyncAccountCatalogCounters_NextCatalogSequence_Positive", "\"NextCatalogSequence\" > 0"));

		builder.HasKey(e => e.AccountId);
		builder.Property(e => e.AccountId).HasMaxLength(100);

		builder.Property(e => e.NextCatalogSequence).IsRequired();

		builder
			.HasOne(e => e.Account)
			.WithOne(e => e.CatalogCounter)
			.HasForeignKey<SyncAccountCatalogCounterEntity>(e => e.AccountId)
			.HasConstraintName("FK_SyncAccountCatalogCounters_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
