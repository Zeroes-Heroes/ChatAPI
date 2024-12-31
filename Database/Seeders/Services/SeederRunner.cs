using Database.Context;
using Database.Seeders.Interfaces;

namespace Database.Seeders.Services
{
    public class SeederRunner(IEnumerable<ISeeder> seeders)
    {
        public void RunSeeders(AppDbContext context)
        {
            foreach(var seeder in seeders)
            {
                seeder.Seed(context);
            }
        }
    }
}