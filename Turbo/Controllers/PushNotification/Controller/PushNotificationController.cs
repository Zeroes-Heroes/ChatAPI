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
        [HttpGet]
        public async Task<IActionResult> GetPushNotificationDeviceData()
        {
            var result = await pushNotificationService.GetPushNotificationData(User.DeviceId(), User.Id());
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

        [HttpPut]
        public async Task<IActionResult> ChangePushNotificationStatus([FromBody] ChangeStatusRequest request)
        {
            var result = await pushNotificationService.ChangePushNotificationStatus(User.Id(), User.DeviceId(), request);
            if (result.IsSuccess)
            {
                return Ok();
            }

            return StatusCode(result.StatusCode, result.Error);
        }
    }
}