using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Services.NotificationDispatch.Interface;
using Services.NotificationDispatch.Models;
using Services.Token.Interface;
using Services.Utilities.Models;

namespace Services.NotificationDispatch.Service
{
    public class AppleService(IAppleTokenService appleTokenService, IOptions<NotificationSettings> notificationSettings, ILogger<AppleService> logger) : IAppleService
    {
        private readonly NotificationSettings notificationSettings = notificationSettings.Value;

        public async Task SendPushNotification(string deviceToken, AppleNotificationPayload payload)
        {
            HttpClient httpClient = new HttpClient();

            string token = await appleTokenService.GetPushNotificationToken();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("bearer", token);

            string bundleId = notificationSettings.AppleBundleId;
            httpClient.DefaultRequestHeaders.Add("apns-topic", bundleId);

            string apnUrl = notificationSettings.ApplePushNotificationUrl + deviceToken;
            string jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);

            logger.LogDebug("Sending Apple push notification. URL: {Url}, Payload: {Payload}", apnUrl, jsonPayload);

            var request = new HttpRequestMessage(HttpMethod.Post, apnUrl)
            {
                // APNs requires HTTP/2; RequestVersionOrHigher ensures it is not downgraded
                Version = new Version(2, 0),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await httpClient.SendAsync(request);

            logger.LogInformation("APNs response for device {DeviceToken}: {StatusCode} {ReasonPhrase}",
                deviceToken, (int)response.StatusCode, response.ReasonPhrase);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                logger.LogError("APNs error for device {DeviceToken}. Status: {StatusCode}, Body: {Body}",
                    deviceToken, (int)response.StatusCode, body);
            }

            response.EnsureSuccessStatusCode();
        }
    }
}