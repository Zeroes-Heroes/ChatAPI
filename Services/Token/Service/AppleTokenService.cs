using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Services.Extensions;
using Services.Token.Interface;
using Services.Utilities.Models;
using Services.Utilities.Statics;

namespace Services.Token.Service
{
    public class AppleTokenService(IOptions<NotificationSettings> notificationSettings, IDistributedCache cache) : IAppleTokenService
    {
        private readonly NotificationSettings notificationSettings = notificationSettings.Value;

        private Task<string> GeneratePushNotificationToken()
        {
            string privateKeyText = File.ReadAllText(notificationSettings.ApplePrivateKeyPath, Encoding.UTF8);

            using ECDsa ecdsa = GetEcdsaParametersFromPrivateKey(privateKeyText);

            long iat = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

            var header = new JwtHeader
        {
            { "alg", SecurityAlgorithms.EcdsaSha256 },
            { "kid", notificationSettings.AppleKeyId},
        };

            var payload = new JwtPayload
        {
            { "iat", iat },
            { "iss", notificationSettings.AppleTeamId },
        };

            string headerJson = JsonSerializer.Serialize(header);
            string payloadJson = JsonSerializer.Serialize(payload);

            string encodeHeader = Base64UrlEncoder.Encode(headerJson);
            string encodePayload = Base64UrlEncoder.Encode(payloadJson);

            string usingToken = $"{encodeHeader}.{encodePayload}";

            var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(usingToken), HashAlgorithmName.SHA256);
            string encodedSignature = Base64UrlEncoder.Encode(signature);

            string tokenString = $"{usingToken}.{encodedSignature}";
            return Task.FromResult(tokenString);
        }

        private static ECDsa GetEcdsaParametersFromPrivateKey(string privateKeyContent)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyContent.ToCharArray());
            return ecdsa;
        }

        public async Task<string> GetPushNotificationToken()
        {
            string? tokenExistsInCacheWithQuotes = await cache.GetStringAsync(string.Format(CacheKeys.ApplePushNotificationToken));

            if (tokenExistsInCacheWithQuotes != null)
            {
                string tokenWithoutQuotes = tokenExistsInCacheWithQuotes.Trim('"');
                return tokenWithoutQuotes;
            }
            string token = await GeneratePushNotificationToken();

            await cache.SetAsync(
                string.Format(CacheKeys.ApplePushNotificationToken),
                token,
                DateTime.UtcNow.AddMinutes(20));

            return token;
        }
    }
}