using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncAccountConfiguration : IEntityTypeConfiguration<SyncAccountEntity>
{
	public void Configure(EntityTypeBuilder<SyncAccountEntity> builder)
	{
		builder.ToTable("SyncAccounts", tb =>
			tb.HasCheckConstraint("CK_SyncAccounts_EndedAtUtc_NotBeforeCreatedAtUtc", "\"EndedAtUtc\" IS NULL OR \"EndedAtUtc\" >= \"CreatedAtUtc\""));

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);

		builder.Property(e => e.CreatedAtUtc).IsRequired();

		builder.Property(e => e.RevocationBarrierActive).IsRequired().HasDefaultValue(false);
		builder.Property(e => e.PreviousAccountId).HasMaxLength(100);
		builder.Property(e => e.FreshStartOperationId).HasMaxLength(128);

		// Filtered unique: one fresh-start operation owns at most one context per identity, so a retry
		// of the same operation can only ever resolve to the context it already created (stage 06
		// idempotency - see SyncAccountEntity.FreshStartOperationId). Null for every ordinary context.
		builder
			.HasIndex(e => new { e.LegacyUserId, e.FreshStartOperationId })
			.IsUnique()
			.HasFilter("\"FreshStartOperationId\" IS NOT NULL")
			.HasDatabaseName("IX_SyncAccounts_LegacyUserId_FreshStartOperation");

		// Self-reference to the superseded context (stage 06 fresh start): recorded for the boundary
		// decision, never traversed to read the old context's data.
		builder
			.HasOne<SyncAccountEntity>()
			.WithMany()
			.HasForeignKey(e => e.PreviousAccountId)
			.HasConstraintName("FK_SyncAccounts_PreviousAccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Filtered: unique only among currently-active contexts (EndedAtUtc IS NULL), so a legacy
		// user can have at most one *current* context while any number of ended ones are retained.
		builder
			.HasIndex(e => e.LegacyUserId)
			.IsUnique()
			.HasFilter("\"EndedAtUtc\" IS NULL")
			.HasDatabaseName("IX_SyncAccounts_LegacyUserId_Active");

		builder
			.HasOne(e => e.LegacyUser)
			.WithMany()
			.HasForeignKey(e => e.LegacyUserId)
			.HasConstraintName("FK_SyncAccounts_LegacyUserId_Users_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
