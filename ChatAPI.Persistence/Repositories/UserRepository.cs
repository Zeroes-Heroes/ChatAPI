using ChatAPI.Application.DTOs.Authorization;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Domain.Entities;
using ChatAPI.Persistence.Database;
using Microsoft.EntityFrameworkCore;

namespace ChatAPI.Persistence.Repositories
{
	public class UserRepository(AppDbContext dbContext) : IUserRepository
	{
		/// <summary>
		/// Add a new user with a user device to the database.
		/// </summary>
		/// <param name="dto">A model containing information about the user and device</param>
		public void AddUser(UserRegisterDTO dto)
		{
			UserDevice userDevice = new UserDevice(
				new User(dto.Name, dto.Phone),
				dto.DeviceId);

			dbContext.UserDevices.Add(userDevice);
		}

		/// <summary>
		/// Gets the UserDevice entity by phone and device id
		/// and includes the User entity of the same user with tracking
		/// </summary>
		/// <param name="phone"></param>
		/// <param name="deviceId"></param>
		/// <returns></returns>
		public Task<UserDevice?> GetUserDeviceUserIncluded(string phone, string deviceId) =>
			dbContext.UserDevices
				.Include(ud => ud.User)
				.Where(d => d.DeviceId == deviceId && d.User.Phone == phone)
				.FirstOrDefaultAsync();
        public Task<User?> GetUserByUserId(int userId) =>
            dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId);

        public Task<User?> GetUserByEmail(string email, bool trackEntity = true)
        {
            IQueryable<User> query = dbContext.Users.AsQueryable();

		public Task SaveChangesAsync() => dbContext.SaveChangesAsync();
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