namespace ChatAPI.Application.UseCases.Abstractions.Repositories
{
    public interface IBaseRepository
    {
        Task SaveChangesAsync();
    }
}
