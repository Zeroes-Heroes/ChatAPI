using Database.Context;

namespace Database.Seeders.Interfaces
{
    public interface ISeeder
    {
        void Seed(AppDbContext context);
    }
}