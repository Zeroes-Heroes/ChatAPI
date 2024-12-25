using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Services.Repositories.User.Interface;
using Services.User.Models;

namespace Services.Repositories.User.Repository;

public class UserRepository(AppDbContext dbContext) : IUserRepository
{
	/// <summary>
	/// Add a new user with a user device to the database.
	/// </summary>
	/// <param name="dto">A model containing information about the user and device</param>
	public void AddUserDevice(UserRegisterDTO dto)
	{
		UserDeviceEntity userDevice = new(
			new UserEntity(dto.Name, dto.Phone),
			dto.DeviceId);

		dbContext.UserDevices.Add(userDevice);
	}

	/// <summary>
	/// Gets the User entity by phone and includes the UserDevices entities of the same user with tracking
	/// </summary>
	public Task<UserEntity?> GetUserIncludingDevicesAndLoginCode(string phone) =>
		dbContext.Users
			.Include(u => u.UserDevices)
			.ThenInclude(u => u.UserLoginCode)
			.Where(u => u.Phone == phone)
			.FirstOrDefaultAsync();

	public Task SaveChangesAsync() => dbContext.SaveChangesAsync();

	public Task<UserEntity?> GetUserByEmailAsNoTracking(string email) =>
		dbContext.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Name == email);

	/// <summary>
	/// Returns whether or not a user with the given phone number exists in the database.
	/// </summary>
	/// <returns>True if the user exists or false if he doesn't.</returns>
	public Task<bool> DoesUserExist(string phone) =>
		dbContext.Users.AnyAsync(u => u.Phone == phone);

	/// <summary>
	/// Returns whether or not a user with the given id exists in the database.
	/// </summary>
	/// <returns>True if the user exists or false if he doesn't.</returns>
	public Task<bool> DoesUserExist(int id) =>
		dbContext.Users.AnyAsync(u => u.Id == id);

	public Task<UserEntity?> GetUserNoTracking(string phone) =>
		dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Phone == phone);

	/// <summary>
	/// Gets the user by the given credentials.
	/// If one of the properties does not match, no entity is returned.
	/// </summary>
	/// <param name="dto">The credentials that will be used to check if the user exists.</param>
	/// <returns>The entity corresponding to the given credentials or null if there isn't one.</returns>
	public Task<UserEntity?> GetUserNoTracking(UserLoginDTO dto) =>
		dbContext.UserDevices
			.Where(ud => ud.DeviceId == dto.DeviceId
				&& ud.User!.Phone == dto.Phone
				&& ud.UserLoginCode!.SecretLoginCode == dto.SecretLoginCode)
			.Select(ud => ud.User)
			.FirstOrDefaultAsync();

	public Task<UserEntity?> GetUserNoTracking(int userId) =>
		dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);

	/// <summary>
	/// Gets the users which correspond to the given ids.
	/// </summary>
	/// <param name="userIds">The user ids of the users requested.</param>
	/// <returns>Array of user entities.</returns>
	public Task<UserEntity[]> GetUsers(int[] userIds) =>
		dbContext.Users.Where(u => userIds.Contains(u.Id)).ToArrayAsync();

	public Task<UserDeviceEntity?> GetIdByDeviceId(string deviceId) =>
		dbContext.UserDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
}