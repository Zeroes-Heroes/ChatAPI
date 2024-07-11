using Services.Resources.Models;
using Services.Utilities;

namespace Services.Resources.Interfaces
{
	public interface IResourceService
	{
		Task<Result<CountryCodesResponseModel[]>> GetCountryCodes();
	}
}
