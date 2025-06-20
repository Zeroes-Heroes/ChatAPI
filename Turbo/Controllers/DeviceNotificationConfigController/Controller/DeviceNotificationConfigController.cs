using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Services.DeviceNotificationConfig.Interface;
using Services.DeviceNotificationConfig.Models;
using Services.Extensions;

namespace Turbo.Controllers.DeviceNotificationConfig.Controller
{
    [ApiController]
    [Route("[controller]")]
    [Authorize]
    public class NotificationController(IDeviceNotificationConfig deviceNotificationService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetPushNotificationDeviceData()
        {
            var result = await deviceNotificationService.GetDeviceDataForNotification(User.DeviceId(), User.Id());
            if (result.IsSuccess)
            {
                return Ok(result.Data);
            }

            return StatusCode(result.StatusCode, result.Error);
        }

        [HttpPost]
        public async Task<IActionResult> SubscribeForNotification([FromBody] DeviceNotificationDTO deviceData)
        {
            var result = await deviceNotificationService.SubscribeDeviceForNotification(deviceData, User.Id(), User.DeviceId());
            if (result.IsSuccess)
            {
                return Ok();
            }

            return StatusCode(result.StatusCode, result.Error);
        }

        [HttpPut]
        public async Task<IActionResult> ChangeNotificationStatus([FromBody] ChangeStatusRequest request)
        {
            var result = await deviceNotificationService.ChangeDeviceNotificationStatus(User.Id(), User.DeviceId(), request);
            if (result.IsSuccess)
            {
                return Ok();
            }

            return StatusCode(result.StatusCode, result.Error);
        }
    }
}