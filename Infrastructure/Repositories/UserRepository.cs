using Application.UseCases.Abstractions;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositoies
{
	public class UserRepository(AppDbContext dbContext) : IUserRepository
	{
		public async Task AddUser(string email, string hashedPassword)
		{
			User user = new(email, hashedPassword);
			dbContext.Users.Add(user);
			await dbContext.SaveChangesAsync();
		}

		public Task<User?> GetUserByEmail(string email) =>
			dbContext.Users.AsQueryable().FirstOrDefaultAsync(x => x.Email == email);
	}
}
