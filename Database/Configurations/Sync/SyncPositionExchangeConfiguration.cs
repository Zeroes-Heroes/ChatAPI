using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncPositionExchangeConfiguration : IEntityTypeConfiguration<SyncPositionExchangeEntity>
{
	public void Configure(EntityTypeBuilder<SyncPositionExchangeEntity> builder)
	{
		builder.ToTable("SyncPositionExchanges", tb =>
			// "Each exchange has a new ExchangeId, starts at BatchIndex=0, increments by one" - a negative
			// index is not a possible batch of any exchange.
			tb.HasCheckConstraint("CK_SyncPositionExchanges_BatchIndex_NonNegative", "\"BatchIndex\" >= 0"));

		// The primary key IS the idempotence rule: one stored answer per (session, exchange, index).
		builder.HasKey(e => new { e.SyncSessionId, e.ExchangeId, e.BatchIndex });

		builder.Property(e => e.SyncSessionId).HasMaxLength(100);
		builder.Property(e => e.ExchangeId).HasMaxLength(128);
		builder.Property(e => e.ConnectionId).IsRequired().HasMaxLength(200);
		builder.Property(e => e.RequestDigest).IsRequired().HasMaxLength(100);
		builder.Property(e => e.ResultJson).IsRequired();
		builder.Property(e => e.Kind).IsRequired();
		builder.Property(e => e.RightsVersion).IsRequired();
		builder.Property(e => e.Final).IsRequired();
		builder.Property(e => e.CreatedAtUtc).IsRequired();

		builder
			.HasOne(e => e.Session)
			.WithMany()
			.HasForeignKey(e => e.SyncSessionId)
			.HasConstraintName("FK_SyncPositionExchanges_SyncSessionId_SyncSessions_Id")
			.OnDelete(DeleteBehavior.Cascade);
	}
}
