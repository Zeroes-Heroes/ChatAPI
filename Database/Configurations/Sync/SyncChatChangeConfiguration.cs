using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncChatChangeConfiguration : IEntityTypeConfiguration<SyncChatChangeEntity>
{
	public void Configure(EntityTypeBuilder<SyncChatChangeEntity> builder)
	{
		builder.ToTable("SyncChatChanges", tb =>
			tb.HasCheckConstraint("CK_SyncChatChanges_ChangeSequence_Positive", "\"ChangeSequence\" > 0"));

		builder.HasKey(e => new { e.ChatId, e.ChangeSequence });

		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.ReferenceId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.SecondaryId).HasMaxLength(150);

		builder.Property(e => e.Kind).IsRequired();
		builder.Property(e => e.OccurredAtUtc).IsRequired();

		builder
			.HasOne(e => e.Chat)
			.WithMany()
			.HasForeignKey(e => e.ChatId)
			.HasConstraintName("FK_SyncChatChanges_ChatId_SyncChats_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
