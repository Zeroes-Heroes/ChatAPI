using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebAPI.Controllers
{
	[ApiController]
	[Route("")]
	public class HomeController : ControllerBase
	{
		[Route("/error")]
		[ApiExplorerSettings(IgnoreApi = true)]
		[AllowAnonymous]
		public IActionResult HandleError() =>
			Problem("Internal server error (500)");
	}
}
