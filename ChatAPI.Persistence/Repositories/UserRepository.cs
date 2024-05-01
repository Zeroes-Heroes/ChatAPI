using ChatAPI.Application.DTOs.User;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Domain.Entities;
using ChatAPI.Persistence.Database;
using EmitMapper;
using Microsoft.EntityFrameworkCore;

namespace ChatAPI.Persistence.Repositories
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

            return query.FirstOrDefaultAsync(x => x.Name == email);
        }

        /// <summary>
        /// Gets the user by the given credentials.
        /// If one of the properties does not match, no entity is returned.
        /// </summary>
        /// <param name="loginCredentials">The credentials that will be used to check if the user exists.</param>
        /// <param name="trackEntity">A boolean parameter instructing if the entity should be tracked or not.</param>
        /// <returns>The entity corresponding to the given credentials or null if there isn't one.</returns>
        public Task<User?> GetUserForLogin(UserLoginV2DTO loginCredentials, bool trackEntity = true)
        {
            IQueryable<User> query = dbContext.Users.AsQueryable();

            if (!trackEntity)
                query = query.AsNoTracking();

            return query.FirstOrDefaultAsync(u => u.Phone == loginCredentials.Phone && u.UserDevices.Any(ud => ud.DeviceId == loginCredentials.DeviceId));
        }
    }
}