using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncMembershipConfiguration : IEntityTypeConfiguration<SyncMembershipEntity>
{
	public void Configure(EntityTypeBuilder<SyncMembershipEntity> builder)
	{
		builder.ToTable("SyncMemberships");

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);
		builder.Property(e => e.ChatId).HasMaxLength(100);
		builder.Property(e => e.AccountId).HasMaxLength(100);
		builder.Property(e => e.EndedByAccountId).HasMaxLength(100);

		builder.Property(e => e.State).IsRequired();
		builder.Property(e => e.RightsVersion).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Covering unique indexes so children can tie (ChatId, MembershipId) and
		// (ChatId, AccountId, MembershipId) with composite FKs - this is what prevents a
		// message/status row from pairing a membership with the wrong chat or the wrong account.
		builder.HasAlternateKey(e => new { e.ChatId, e.Id }).HasName("AK_SyncMemberships_ChatId_Id");
		builder.HasAlternateKey(e => new { e.ChatId, e.AccountId, e.Id }).HasName("AK_SyncMemberships_ChatId_AccountId_Id");

		builder.HasIndex(e => new { e.ChatId, e.AccountId });

		builder
			.HasOne(e => e.Chat)
			.WithMany(e => e.Memberships)
			.HasForeignKey(e => e.ChatId)
			.HasConstraintName("FK_SyncMemberships_ChatId_SyncChats_Id")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne(e => e.Account)
			.WithMany(e => e.Memberships)
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncMemberships_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		builder
			.HasOne(e => e.EndedByAccount)
			.WithMany()
			.HasForeignKey(e => e.EndedByAccountId)
			.HasConstraintName("FK_SyncMemberships_EndedByAccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
