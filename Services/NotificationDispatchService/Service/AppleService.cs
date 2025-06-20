using System.Text;
using Microsoft.Extensions.Options;
using Services.NotificationDispatch.Interface;
using Services.NotificationDispatch.Models;
using Services.Token.Interface;
using Services.Utilities.Models;

namespace Services.NotificationDispatch.Service
{
    public class AppleService(IAppleTokenService appleTokenService, IOptions<NotificationSettings> notificationSettings) : IAppleService
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
            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);

            var request = new HttpRequestMessage(HttpMethod.Post, apnUrl)
            {
                Version = new Version(2, 0),
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
    }
}