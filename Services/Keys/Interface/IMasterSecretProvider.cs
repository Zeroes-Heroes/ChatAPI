namespace Services.Keys.Interface;

/// <summary>
/// Supplies the server's master secret(s) that protect message-key records at rest. Per
/// docs/sync-contract-v1.md: "Master secret is mounted read-only outside PostgreSQL behind a
/// replaceable provider in the later key module ... Retain old master versions through rewrap."
/// Implementations must never read the secret from PostgreSQL, from application configuration
/// values embedded in source/appsettings, or from any other DB-reachable fallback - only from the
/// read-only mounted location itself - so that a PostgreSQL dump alone can never unlock keys.
/// </summary>
public interface IMasterSecretProvider
{
	/// <summary>The version new protection operations should use. Does not change which versions can still be used to unprotect existing rows.</summary>
	string CurrentVersion { get; }

	/// <summary>
	/// Returns the 32-byte key material for <paramref name="version"/>. Each call returns a fresh,
	/// independent array - never the same instance (or a view over the same backing storage) twice,
	/// even across repeated calls for the same version. The caller owns the returned array and may
	/// freely mutate or zero it after use; doing so must never affect any other caller or any later
	/// call to this method.
	/// </summary>
	/// <exception cref="Services.Keys.MasterSecretUnavailableException">The version is not mounted/readable, or is the wrong length.</exception>
	byte[] GetKey(string version);
}
