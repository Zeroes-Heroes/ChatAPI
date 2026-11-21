using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Services.Keys.Interface;

namespace Services.Keys.Service;

/// <summary>
/// Reads master key material from a read-only mounted directory, one file per version named
/// "{version}.key" holding exactly 32 raw bytes - never from PostgreSQL, never from a config value
/// carrying the key bytes themselves (only the directory path/version name are configuration).
/// Fails fast at construction if misconfigured or if <see cref="CurrentVersion"/> is not readable,
/// per docs/sync-contract-v1.md: "Missing/incorrect secret rejects."
/// </summary>
public sealed class FileMasterSecretProvider : IMasterSecretProvider
{
	private const int RequiredKeyLength = 32;
	private readonly string _directoryPath;
	private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

	public string CurrentVersion { get; }

	public FileMasterSecretProvider(IOptions<MasterSecretOptions> options)
	{
		MasterSecretOptions value = options.Value;
		if (string.IsNullOrWhiteSpace(value.DirectoryPath))
		{
			throw new MasterSecretUnavailableException("Master secret directory is not configured.");
		}
		if (string.IsNullOrWhiteSpace(value.CurrentVersion))
		{
			throw new MasterSecretUnavailableException("Master secret current version is not configured.");
		}
		if (!IsValidVersion(value.CurrentVersion))
		{
			throw new MasterSecretUnavailableException("Master secret current version is not a valid version id.");
		}
		if (!Directory.Exists(value.DirectoryPath))
		{
			throw new MasterSecretUnavailableException($"Master secret directory '{value.DirectoryPath}' does not exist or is not mounted.");
		}

		_directoryPath = value.DirectoryPath;
		CurrentVersion = value.CurrentVersion;

		// Fail fast: the current version must be usable now, not discovered missing on the first protect call.
		_ = GetKey(CurrentVersion);
	}

	/// <summary>
	/// Returns a fresh defensive copy every call - never the same backing array instance twice. The
	/// cache holds one array per version for the process lifetime; without a clone here, any caller
	/// that mutates its "own" returned array (e.g. zeroing it after use - a textbook, reasonable-
	/// looking key-hygiene habit) would corrupt the cached master key for every subsequent
	/// Protect/Unprotect call, for every tenant, until process restart. Callers own what they
	/// receive and may freely mutate/clear it.
	/// </summary>
	public byte[] GetKey(string version)
	{
		if (!IsValidVersion(version))
		{
			throw new MasterSecretUnavailableException("Master key version is not a valid version id.");
		}
		return (byte[])_cache.GetOrAdd(version, LoadFromDisk).Clone();
	}

	private byte[] LoadFromDisk(string version)
	{
		string path = Path.Combine(_directoryPath, $"{version}.key");
		byte[] bytes;
		try
		{
			bytes = File.ReadAllBytes(path);
		}
		catch (FileNotFoundException ex)
		{
			throw new MasterSecretUnavailableException($"Master secret version '{version}' is not mounted.", ex);
		}
		catch (DirectoryNotFoundException ex)
		{
			throw new MasterSecretUnavailableException("Master secret directory is not mounted.", ex);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new MasterSecretUnavailableException($"Master secret version '{version}' is not readable.", ex);
		}

		if (bytes.Length != RequiredKeyLength)
		{
			throw new MasterSecretUnavailableException($"Master secret version '{version}' must be exactly {RequiredKeyLength} bytes.");
		}
		return bytes;
	}

	/// <summary>Restricted to a filename-safe id so a version string can never path-traverse out of <see cref="_directoryPath"/>.</summary>
	private static bool IsValidVersion(string version)
	{
		if (version.Length is 0 or > 32)
		{
			return false;
		}
		foreach (char c in version)
		{
			bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '-' or '_';
			if (!ok)
			{
				return false;
			}
		}
		return true;
	}
}
