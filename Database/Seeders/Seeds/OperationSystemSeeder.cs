using Database.Context;
using Database.Entities;
using Database.Seeders.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Database.Seeders
{
    public class OperationSystemSeeder() : ISeeder
    {
        public void Seed(AppDbContext context)
        {
            if (!context.OperationSystem.Any())
            {
                var entity = new List<OperationSystemEntity>
                {
                    new() { TypeOS = "ios" },
                    new() { TypeOS = "android" },
                };

                context.OperationSystem.AddRange(entity);
                context.SaveChanges();
            }
        }
    }
}