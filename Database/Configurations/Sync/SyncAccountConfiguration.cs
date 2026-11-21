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
