namespace Database.Context;

using Database.Configurations;
using Database.Entities;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
	public void ApplyMigrations() => Database.Migrate();

	public DbSet<UserEntity> Users { get; set; }
	public DbSet<UserDeviceEntity> UserDevices { get; set; }
	public DbSet<FriendshipEntity> Friendships { get; set; }
	public DbSet<UserLoginCodeEntity> UserLoginCodes { get; set; }

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.ApplyConfiguration(new UserConfiguration());
		modelBuilder.ApplyConfiguration(new UserDeviceConfiguration());
		modelBuilder.ApplyConfiguration(new UserLoginCodeConfiguration());
		modelBuilder.ApplyConfiguration(new FriendshipConfiguration());
	}
}