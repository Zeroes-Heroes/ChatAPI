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
		public void AddUserDevice(UserRegisterDTO dto)
		{
			UserDevice userDevice = new UserDevice(
				new User(dto.Name, dto.Phone),
				dto.DeviceId);

			dbContext.UserDevices.Add(userDevice);
		}

		/// <summary>
		/// Gets the User entity by phone and includes the UserDevices entities of the same user with tracking
		/// </summary>
		public Task<User?> GetUserWithUserDevicesIncluded(string phone) =>
			dbContext.Users
				.Include(u => u.UserDevices)
				.Where(u => u.Phone == phone)
				.FirstOrDefaultAsync();

		public Task SaveChangesAsync() => dbContext.SaveChangesAsync();

		public Task<User?> GetUserByEmailAsNoTracking(string email) =>
			dbContext.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Name == email);

		/// <summary>
		/// Returns whether or not a user with the given phone number exists in the database.
		/// </summary>
		/// <returns>True if the user exists or false if he doesn't.</returns>
		public Task<bool> DoesUserExist(string phone) =>
			dbContext.Users.AnyAsync(u => u.Phone == phone);

		public Task<User?> GetUser(string phone) =>
			dbContext.Users.FirstOrDefaultAsync(u => u.Phone == phone);

		/// <summary>
		/// Gets the user by the given credentials.
		/// If one of the properties does not match, no entity is returned.
		/// </summary>
		/// <param name="dto">The credentials that will be used to check if the user exists.</param>
		/// <returns>The entity corresponding to the given credentials or null if there isn't one.</returns>
		public Task<User?> GetUserForLoginNoTracking(UserLoginDTO dto) =>
			dbContext.UserDevices
				.Where(ud => ud.DeviceId == dto.DeviceId
					&& ud.User!.Phone == dto.Phone
					&& ud.UserLoginCode!.SecretLoginCode == dto.SecretLoginCode)
				.Select(ud => ud.User)
				.FirstOrDefaultAsync();
	}
}