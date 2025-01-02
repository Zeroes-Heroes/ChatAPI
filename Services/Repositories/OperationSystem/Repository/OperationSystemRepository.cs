using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Services.Repositories.OperationSystem.Interface;

namespace Services.Repositories.OperationSystem.Repository
{
    public class OperationSystemRepository(AppDbContext dbContext) : IOperationSystemRepository
    {
        public async Task<int?> GetOperationSystemId(string operationSystem)
        {
            OperationSystemEntity result = await dbContext.OperationSystems.FirstOrDefaultAsync(o => o.TypeOS == operationSystem);
            if (result != null)
            {
                return result.Id;
            }
            return null;
        }

        public Task SaveChangesAsync() =>
            dbContext.SaveChangesAsync();
    }
}