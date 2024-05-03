using ChatAPI.Persistence.Configurations;
using ChatAPI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatAPI.Persistence.Database
{
    public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
		public void ApplyMigrations() => Database.Migrate();

		public DbSet<User> Users { get; set; }
		public DbSet<UserDevice> UserDevices { get; set; }
		public DbSet<Friendship> Friendships { get; set; }
		public DbSet<UserLoginCode> UserLoginCodes { get; set; }

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			modelBuilder.ApplyConfiguration(new UserConfiguration());
			modelBuilder.ApplyConfiguration(new UserDeviceConfiguration());
			modelBuilder.ApplyConfiguration(new UserLoginCodeConfiguration());
		}
	}
}