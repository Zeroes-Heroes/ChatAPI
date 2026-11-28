using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncRefreshTokenRotationConfiguration : IEntityTypeConfiguration<SyncRefreshTokenRotationEntity>
{
	public void Configure(EntityTypeBuilder<SyncRefreshTokenRotationEntity> builder)
	{
		builder.ToTable("SyncRefreshTokenRotations", tb =>
		{
			tb.HasCheckConstraint("CK_SyncRefreshTokenRotations_ReplayExpiresNotBeforeCreated", "\"ReplayExpiresAtUtc\" >= \"CreatedAtUtc\"");
			tb.HasCheckConstraint("CK_SyncRefreshTokenRotations_ReplayPayloadFieldsTogether",
				"(\"ProtectedReplayPayload\" IS NULL AND \"ProtectedReplayNonce\" IS NULL AND \"ProtectedReplayTag\" IS NULL AND \"ProtectedReplayMasterKeyVersion\" IS NULL)" +
				" OR (\"ProtectedReplayPayload\" IS NOT NULL AND \"ProtectedReplayNonce\" IS NOT NULL AND \"ProtectedReplayTag\" IS NOT NULL AND \"ProtectedReplayMasterKeyVersion\" IS NOT NULL)");
		});

		// (SessionId, RefreshOperationId) as the primary key IS the idempotency boundary - see the
		// entity's own doc comment.
		builder.HasKey(e => new { e.SessionId, e.RefreshOperationId });

		builder.Property(e => e.SessionId).HasMaxLength(100);
		builder.Property(e => e.RefreshOperationId).HasMaxLength(150);
		builder.Property(e => e.OldRefreshTokenHash).IsRequired();
		builder.Property(e => e.NewRefreshTokenHash).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();
		builder.Property(e => e.ReplayExpiresAtUtc).IsRequired();
		builder.Property(e => e.ProtectedReplayMasterKeyVersion).HasMaxLength(32);

		// Reuse-detection scan: "did this hash ever appear in this session's rotation history".
		builder.HasIndex(e => new { e.SessionId, e.OldRefreshTokenHash }).HasDatabaseName("IX_SyncRefreshTokenRotations_SessionId_OldHash");
		builder.HasIndex(e => new { e.SessionId, e.NewRefreshTokenHash }).HasDatabaseName("IX_SyncRefreshTokenRotations_SessionId_NewHash");

		builder
			.HasOne(e => e.Session)
			.WithMany(e => e.Rotations)
			.HasForeignKey(e => e.SessionId)
			.HasConstraintName("FK_SyncRefreshTokenRotations_SessionId_SyncDeviceSessions_Id")
			.OnDelete(DeleteBehavior.Restrict);
	}
}
