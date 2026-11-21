using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncLegacyContextExclusionConfiguration : IEntityTypeConfiguration<SyncLegacyContextExclusionEntity>
{
	public void Configure(EntityTypeBuilder<SyncLegacyContextExclusionEntity> builder)
	{
		builder.ToTable("SyncLegacyContextExclusions");

		builder.HasKey(e => new { e.AccountId, e.LegacyChatId });
		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		builder
			.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncLegacyContextExclusions_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Cascade);

		// Cascade from the legacy chat: if a chat row ever disappears, its exclusion is meaningless.
		builder
			.HasOne<Database.Entities.ChatEntity>()
			.WithMany()
			.HasForeignKey(e => e.LegacyChatId)
			.HasConstraintName("FK_SyncLegacyContextExclusions_LegacyChatId_Chats_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
