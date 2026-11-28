using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncChatSequenceCounterConfiguration : IEntityTypeConfiguration<SyncChatSequenceCounterEntity>
{
	public void Configure(EntityTypeBuilder<SyncChatSequenceCounterEntity> builder)
	{
		builder.ToTable("SyncChatSequenceCounters", tb =>
		{
			// "Next to allocate" is always a real, positive sequence - 0 never appears here even
			// before the first allocation (see Database.Sequencing.SyncSequenceConventions).
			tb.HasCheckConstraint("CK_SyncChatSequenceCounters_NextMessageSequence_Positive", "\"NextMessageSequence\" > 0");
			tb.HasCheckConstraint("CK_SyncChatSequenceCounters_NextChangeSequence_Positive", "\"NextChangeSequence\" > 0");
		});

		builder.HasKey(e => e.ChatId);
		builder.Property(e => e.ChatId).HasMaxLength(100);

		builder.Property(e => e.NextMessageSequence).IsRequired();
		builder.Property(e => e.NextChangeSequence).IsRequired();

		builder
			.HasOne(e => e.Chat)
			.WithOne(e => e.SequenceCounter)
			.HasForeignKey<SyncChatSequenceCounterEntity>(e => e.ChatId)
			.HasConstraintName("FK_SyncChatSequenceCounters_ChatId_SyncChats_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
