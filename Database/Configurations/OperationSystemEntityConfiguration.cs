using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Database.Configurations
{
    internal class OperationSystemEntityConfiguration : IEntityTypeConfiguration<OperationSystemEntity>
    {
        public void Configure(EntityTypeBuilder<OperationSystemEntity> builder)
        {
            builder.ToTable("OperationSystems");
        }
    }
}