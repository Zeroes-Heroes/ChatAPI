using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Services.Extensions;
using Services.PushNotification.Interface;
using Services.PushNotification.Models;

namespace Turbo.Controllers.PushNotification.Controller
{
    [ApiController]
    [Route("[controller]")]
    [Authorize]
    public class PushNotificationController(IPushNotification pushNotificationService) : ControllerBase
    {
        [HttpGet("{deviceId}")]
        public async Task<IActionResult> GetPushNotificationDeviceData([FromRoute] string deviceId)
        {
            var result = await pushNotificationService.GetDeviceDataForPushNotification(deviceId, User.Id());
            if (result.IsSuccess)
            {
                return Ok(result.Data);
            }

            return StatusCode(result.StatusCode, result.Error);
        }

        [HttpPost]
        public async Task<IActionResult> SubscribeForPushNotification([FromBody] PushNotificationDTO deviceData)
        {
            var result = await pushNotificationService.SubscribeForPushNotification(deviceData, User.Id());
            if (result.IsSuccess)
            {
                return Ok(result.Data);
            }

            return StatusCode(result.StatusCode, result.Error);
        }

        [HttpPut("{notificationId:int}")]
        // Move to Body
        public async Task<IActionResult> UnsubscribeForPushNotification([FromRoute] int notificationId)
        {
            var result = await pushNotificationService.UnsubscribeForPushNotification(notificationId);
            if (result.IsSuccess)
            {
                return Ok();
            }

            return StatusCode(result.StatusCode, result.Error);
        }
    }
}