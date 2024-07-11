using Microsoft.AspNetCore.Mvc;
using Services.Resources.Interfaces;
using Services.Resources.Models;
using Services.Utilities;

namespace Turbo.Controllers.Resources.Controller
{
	[ApiController]
	[Route("[controller]")]
	public class ResourcesController(IResourceService resourcesService) : ControllerBase
	{
		[HttpGet("country-codes")]
		public async Task<ActionResult<CountryCodesResponseModel[]>> GetCountryCodes()
		{
			Result<CountryCodesResponseModel[]> result = await resourcesService.GetCountryCodes();

			if (result.IsSuccess)
				return result.Data;

			return StatusCode(result.StatusCode, result.Error);
		}
	}
}
