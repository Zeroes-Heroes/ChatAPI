using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Services.Repositories.Resources.Interface;

namespace Services.Repositories.Resources.Repository
{
	internal class ResourceRepository(AppDbContext dbContext) : IResourceRepository
	{
		public Task<CountryCodesEntity[]> GetCountryCodes() =>
			dbContext
			.CountryCodes
			.AsNoTracking()
			.ToArrayAsync();

		public Task SaveChangesAsync() =>
			dbContext.SaveChangesAsync();
	}
}
