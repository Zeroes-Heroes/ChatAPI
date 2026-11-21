using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMessageConfiguration : IEntityTypeConfiguration<SyncMessageEntity>
{
	public void Configure(EntityTypeBuilder<SyncMessageEntity> builder)
	{
		builder.ToTable("SyncMessages", tb =>
		{
			// Real sequences start at 1; 0 is reserved for "nothing applied yet" on the wire (see
			// Database.Sequencing.SyncSequenceConventions).
			tb.HasCheckConstraint("CK_SyncMessages_MessageSequence_Positive", "\"MessageSequence\" > 0");
			tb.HasCheckConstraint("CK_SyncMessages_ChangeSequence_Positive", "\"ChangeSequence\" > 0");
		});

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.SenderAccountId).HasMaxLength(100);
		builder.Property(e => e.SenderMembershipId).HasMaxLength(100);
		builder.Property(e => e.AuthorKeyId).HasMaxLength(100).IsRequired();
		builder.Property(e => e.AuthorKeyVersion).HasMaxLength(50).IsRequired();
		builder.Property(e => e.PackageDigest).HasMaxLength(100).IsRequired();
		builder.Property(e => e.SignedPackage).IsRequired();

		builder.Property(e => e.OperationKind).IsRequired();
		builder.Property(e => e.MessageSequence).IsRequired();
		builder.Property(e => e.ChangeSequence).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Covering unique index so recipient/device status rows can tie (ChatId, MessageId).
		builder.HasAlternateKey(e => new { e.ChatId, e.Id }).HasName("AK_SyncMessages_ChatId_Id");

		// Covering unique index so a SyncOperationResult's (ChatId, AccountId, MessageId) FK can
		// enforce that the account owning the terminal result is the message's actual sender -
		// mirrors the (ChatId, AccountId, Id) alternate key already used for SyncMemberships.
		builder.HasAlternateKey(e => new { e.ChatId, e.SenderAccountId, e.Id }).HasName("AK_SyncMessages_ChatId_SenderAccountId_Id");

		builder.HasIndex(e => new { e.ChatId, e.MessageSequence }).IsUnique();
		builder.HasIndex(e => new { e.ChatId, e.ChangeSequence }).IsUnique();

		builder
			.HasOne(e => e.Chat)
			.WithMany(e => e.Messages)
			.HasForeignKey(e => e.ChatId)
			.HasConstraintName("FK_SyncMessages_ChatId_SyncChats_Id")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne(e => e.SenderAccount)
			.WithMany()
			.HasForeignKey(e => e.SenderAccountId)
			.HasConstraintName("FK_SyncMessages_SenderAccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite (single relationship - a nav property can only back one FK): the sender's
		// membership must belong to both this chat and this account at once, so a message cannot
		// be attributed to a membership stint from a different chat or a different account.
		builder
			.HasOne(e => e.SenderMembership)
			.WithMany()
			.HasForeignKey(e => new { e.ChatId, e.SenderAccountId, e.SenderMembershipId })
			.HasPrincipalKey(m => new { m.ChatId, m.AccountId, m.Id })
			.HasConstraintName("FK_SyncMessages_ChatId_SenderAccountId_SenderMembershipId_SyncMemberships")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
