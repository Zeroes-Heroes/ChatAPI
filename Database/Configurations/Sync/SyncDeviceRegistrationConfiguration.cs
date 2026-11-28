using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncDeviceRegistrationConfiguration : IEntityTypeConfiguration<SyncDeviceRegistrationEntity>
{
	public void Configure(EntityTypeBuilder<SyncDeviceRegistrationEntity> builder)
	{
		builder.ToTable("SyncDeviceRegistrations", tb =>
			tb.HasCheckConstraint("CK_SyncDeviceRegistrations_RevokedAtUtc_RequiresRevokedState",
				"(\"TrustState\" = 2 AND \"RevokedAtUtc\" IS NOT NULL) OR (\"TrustState\" != 2 AND \"RevokedAtUtc\" IS NULL)"));

		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).HasMaxLength(100);
		builder.Property(e => e.AccountId).HasMaxLength(100);

		builder.Property(e => e.CreatedAtUtc).IsRequired();

		builder.Property(e => e.KeyId).IsRequired().HasMaxLength(128);
		builder.Property(e => e.PublicKeySpki).IsRequired();
		builder.Property(e => e.TrustState).IsRequired().HasConversion<int>();
		builder.Property(e => e.TrustVersion).IsRequired();
		builder.Property(e => e.IsFirstDevice).IsRequired();
		builder.Property(e => e.ApprovedByDeviceRegistrationId).HasMaxLength(100);

		// Covering unique index so children can tie (AccountId, DeviceRegistrationId) with a composite FK.
		builder.HasAlternateKey(e => new { e.AccountId, e.Id }).HasName("AK_SyncDeviceRegistrations_AccountId_Id");

		// At most one currently-first device per account: the atomic first-device decision
		// (IDeviceTrustService) must never allow two concurrent enrollments to both win "no trusted
		// device yet, no revocation barrier" and both be stamped IsFirstDevice - this filtered unique
		// index makes that a DB-level guarantee, not just an application-level race window.
		builder
			.HasIndex(e => e.AccountId)
			.IsUnique()
			.HasFilter("\"IsFirstDevice\" = TRUE")
			.HasDatabaseName("IX_SyncDeviceRegistrations_AccountId_FirstDevice");

		builder
			.HasOne(e => e.Account)
			.WithMany(e => e.Devices)
			.HasForeignKey(e => e.AccountId)
			.HasConstraintName("FK_SyncDeviceRegistrations_AccountId_SyncAccounts_Id")
			.OnDelete(DeleteBehavior.Restrict);

		// Composite: the approving device must belong to the same account as the device it approved.
		builder
			.HasOne(e => e.ApprovedByDevice)
			.WithMany()
			.HasForeignKey(e => new { e.AccountId, e.ApprovedByDeviceRegistrationId })
			.HasPrincipalKey(d => new { d.AccountId, d.Id })
			.HasConstraintName("FK_SyncDeviceRegistrations_AccountId_ApprovedBy_SyncDeviceRegistrations")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
