using Database.Context;
using Database.Entities;
using Database.Seeders.Interfaces;

namespace Database.Seeders
{
    public class OperationSystemSeeder() : ISeeder
    {
        public void Seed(AppDbContext context)
        {
            if (!context.OperationSystems.Any())
            {
                List<OperationSystemEntity> entities =
                [
                    new("ios"),
                    new("android"),
                ];

                context.OperationSystems.AddRange(entities);
                context.SaveChanges();
            }
        }
    }
}