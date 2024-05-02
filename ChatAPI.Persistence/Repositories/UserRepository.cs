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
		public Task<UserDevice?> GetUserDeviceByDeviceIdAndPhoneWithUserIncluded(string phone, string deviceId) =>
			dbContext.UserDevices
				.Include(ud => ud.User)
				.Where(d => d.DeviceId == deviceId && d.User.Phone == phone)
				.FirstOrDefaultAsync();

		public Task SaveChangesAsync() => dbContext.SaveChangesAsync();

		public Task<User?> GetUserByEmailAsNoTracking(string email) =>
			dbContext.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Name == email);
	}
}