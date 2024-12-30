using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Services.PushNotification.Interface;
using Services.Token.Interface;
using Services.Utilities;
using Services.Utilities.Models;

namespace Services.PushNotification.Service
{
    public class ApplePushNotificationService(HttpClient httpClient, IAppleTokenService appleTokenService, IOptions<AppSettings> appSettings, IDistributedCache cache) : IApplePushNotificationService
    {
        private readonly AppSettings appSettings = appSettings.Value;

        public async Task<Result> SendAsyncPushNotification(string deviceToken, object payload) // TODO replace "object" with "interface"
        {
            string token = await appleTokenService.GeneratePushNotificationToken();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("bearer", token);

            string bundleId = appSettings.AppleBundleId;
            httpClient.DefaultRequestHeaders.Add("apns-topic", bundleId);

            string apnUrl = appSettings.ApplePushNotificationUrl + deviceToken;
            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);

            var request = new HttpRequestMessage(HttpMethod.Post, apnUrl)
            {
                Version = new Version(2, 0),
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                return Result.Failure($"Failed to send notification: {response.StatusCode}, {responseBody}");
            }
        }
    }
}