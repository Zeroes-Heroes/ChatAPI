using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Infrastructure.Caching
{
	public static class RedisCacheExtension
	{
		/// <summary>
		/// An extension and generic method used to easily store all types of values in the cache.
		/// </summary>
		/// <param name="key">The cache key used to later retrieve the value.</param>
		/// <param name="value">The value to cache.</param>
		/// <param name="expiration">Specific DateTime for expiry date. (e.g. DateTime.UtcNow.AddHours(2))</param>
		public static async Task SetAsync<T>(this IDistributedCache cache, string key, T value, DateTime expiration)
		{
			DistributedCacheEntryOptions options = new() { AbsoluteExpiration = expiration };
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
			await cache.SetAsync(key, bytes, options);
		}

		/// <summary>
		/// An extension method used to easily store string values in the cache.
		/// </summary>
		/// <param name="key">The cache key used to later retrieve the value.</param>
		/// <param name="value">The value to cache.</param>
		/// <param name="expiration">Specific DateTime for expiry date. (e.g. DateTime.UtcNow.AddHours(2))</param>
		public static async Task SetStringAsync(this IDistributedCache cache, string key, string value, DateTime expiration)
		{
			DistributedCacheEntryOptions options = new() { AbsoluteExpiration = expiration };
			await cache.SetStringAsync(key, value, options);
		}
	}
}
