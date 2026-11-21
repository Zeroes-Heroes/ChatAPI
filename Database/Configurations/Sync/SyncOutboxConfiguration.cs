using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncOutboxConfiguration : IEntityTypeConfiguration<SyncOutboxEntity>
{
	public void Configure(EntityTypeBuilder<SyncOutboxEntity> builder)
	{
		builder.ToTable("SyncOutbox");

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).UseIdentityAlwaysColumn();

		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.MessageId).HasMaxLength(100);
		builder.Property(e => e.LeaseOwner).HasMaxLength(100);

		builder.Property(e => e.Status).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Claim the next batch of work: unleased/expired-lease pending-or-failed rows, oldest first.
		builder.HasIndex(e => new { e.Status, e.LeasedUntilUtc, e.Id });

		// Composite: the message must belong to this row's chat.
		builder
			.HasOne(e => e.Message)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.MessageId })
			.HasPrincipalKey(m => new { m.ChatId, m.Id })
			.HasConstraintName("FK_SyncOutbox_ChatId_MessageId_SyncMessages")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
