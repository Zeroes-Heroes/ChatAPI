using Services.Repositories.Resources.Interface;
using Services.Resources.Models;
using Services.Utilities;
using IResourceService = Services.Resources.Interfaces.IResourceService;

namespace Services.Resources.Service
{
	public class ResourcesService(IResourceRepository resourceRepository) : IResourceService
	{
		public async Task<Result<CountryCodesResponseModel[]>> GetCountryCodes()
		{
			return Result<CountryCodesResponseModel[]>
					.Success((await resourceRepository.GetCountryCodes())
						.Select(cc => new CountryCodesResponseModel(cc.Country, cc.Code, cc.Icon))
						.ToArray());
		}
	}
}
