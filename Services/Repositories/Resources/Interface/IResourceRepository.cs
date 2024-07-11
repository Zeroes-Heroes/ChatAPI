using Database.Entities;
using Services.Repositories.Base.Interface;

namespace Services.Repositories.Resources.Interface
{
	public interface IResourceRepository : IBaseRepository
	{
		Task<CountryCodesEntity[]> GetCountryCodes();
	}
}
