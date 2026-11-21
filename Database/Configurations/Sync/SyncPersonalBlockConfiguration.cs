using Database.Entities;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations.Sync;

internal class SyncPersonalBlockConfiguration : IEntityTypeConfiguration<SyncPersonalBlockEntity>
{
	public void Configure(EntityTypeBuilder<SyncPersonalBlockEntity> builder)
	{
		builder.ToTable("SyncPersonalBlocks", tb =>
			// Nobody can block themselves: the pair is the identity of the relation, and a self-row would
			// make every "has anyone blocked me" query have to special-case the caller.
			tb.HasCheckConstraint("CK_SyncPersonalBlocks_NotSelf", "\"BlockerUserId\" <> \"SubjectUserId\""));

		// The ordered pair IS the identity, so a retried block updates one row instead of inserting a
		// second record of the same fact - see the entity's own doc comment.
		builder.HasKey(e => new { e.BlockerUserId, e.SubjectUserId });

		builder.Property(e => e.CreatedAtUtc).IsRequired();

		// "Who has blocked me" is the hot direction (the correspondence gate and the profile resolver both
		// ask it for a set of candidates), and it is not served by the primary key's leading column.
		builder.HasIndex(e => new { e.SubjectUserId, e.BlockerUserId });

		builder
			.HasOne(e => e.Blocker)
			.WithMany()
			.HasForeignKey(e => e.BlockerUserId)
			.HasConstraintName("FK_SyncPersonalBlocks_BlockerUserId_Users_Id")
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(e => e.Subject)
			.WithMany()
			.HasForeignKey(e => e.SubjectUserId)
			.HasConstraintName("FK_SyncPersonalBlocks_SubjectUserId_Users_Id")
			.OnDelete(DeleteBehavior.Cascade);

		builder.Ignore(e => e.IsActive);
	}
}
