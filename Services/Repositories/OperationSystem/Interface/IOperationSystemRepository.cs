using Services.Repositories.Base.Interface;

namespace Services.Repositories.OperationSystem.Interface
{
    public interface IOperationSystemRepository : IBaseRepository
    {
        Task<int?> GetOperationSystemId(string operationSystem);
    }
}