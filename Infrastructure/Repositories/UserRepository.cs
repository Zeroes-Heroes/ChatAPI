using Application.DTOs.User;
using Application.UseCases.Abstractions;
using Domain.Entities;
using EmitMapper;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositoies
{
	public class UserRepository(AppDbContext dbContext) : IUserRepository
	{
		public async Task AddUser(UserRegisterDTO dto)
		{
			Mapper<UserRegisterDTO, User> mapper = Mapper.Default.GetMapper<UserRegisterDTO, User>();
			User user = mapper.Map(dto);
			user.CreatedOn = DateTime.UtcNow;
			dbContext.Users.Add(user);
			await dbContext.SaveChangesAsync();
		}

		public Task<User?> GetUserByUserId(int userId) =>
			dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId);

		public Task<User?> GetUserByEmail(string email, bool trackEntity = true)
		{
			IQueryable<User> query = dbContext.Users.AsQueryable();

			if (!trackEntity)
				query = query.AsNoTracking();

			return query.FirstOrDefaultAsync(x => x.Email == email);
		}
	}
}
