using Application.Models.Authorization;

namespace Application.UseCases.Abstractions
{
    public interface IUserService
    {   
		Task<TokensDTO?> Login(string email, string password);
		Task Logout(int userId);
		Task Register(string email, string password);
    }
}
