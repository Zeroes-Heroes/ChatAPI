using Database.Entities.Sync;
using Database.Enums.Sync;
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
		builder.Property(e => e.AddedByAccountId).HasMaxLength(100);

		builder.Property(e => e.State).IsRequired();
		builder.Property(e => e.RightsVersion).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// Covering unique indexes so children can tie (ChatId, MembershipId) and
		// (ChatId, AccountId, MembershipId) with composite FKs - this is what prevents a
		// message/status row from pairing a membership with the wrong chat or the wrong account.
		builder.HasAlternateKey(e => new { e.ChatId, e.Id }).HasName("AK_SyncMemberships_ChatId_Id");
		builder.HasAlternateKey(e => new { e.ChatId, e.AccountId, e.Id }).HasName("AK_SyncMemberships_ChatId_AccountId_Id");

		builder.HasIndex(e => new { e.ChatId, e.AccountId });

		// Stage 07 (plan/07-membership.md: "Активен участник не се добавя повторно за разширяване на
		// историята"): at most ONE active stint per account per chat, enforced by the database rather
		// than only by ChatMembershipService's own check - a concurrent double add cannot produce two
		// active stints whose history ranges would silently widen the member's access. Frozen stints are
		// deliberately outside the filter: a chat accumulates arbitrarily many of them (leave/rejoin).
		// Named overload on purpose: EF keys an unnamed index by its property set, so reusing
		// `HasIndex(e => new { e.ChatId, e.AccountId })` here would reconfigure the index above instead
		// of declaring a second one over the same columns.
		builder.HasIndex(e => new { e.ChatId, e.AccountId }, "IX_SyncMemberships_ChatId_AccountId_Active")
			.IsUnique()
			.HasFilter($"\"State\" = {(int)SyncMembershipState.Active}");

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

		builder
			.HasOne(e => e.AddedByAccount)
			.WithMany()
			.HasForeignKey(e => e.AddedByAccountId)
			.HasConstraintName("FK_SyncMemberships_AddedByAccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
