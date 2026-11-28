namespace Services.Keys;

/// <summary>
/// Strict configuration for <see cref="Service.FileMasterSecretProvider"/>. Both values are
/// required with no implicit default/fallback: an unconfigured key module must fail to start
/// rather than silently run with no, or a guessed, master secret.
/// </summary>
public sealed class MasterSecretOptions
{
	/// <summary>Directory containing one file per master key version, named "{version}.key", holding exactly 32 raw bytes each. Mounted read-only outside PostgreSQL (e.g. a Kubernetes Secret volume).</summary>
	public string? DirectoryPath { get; set; }

	/// <summary>The version new protection operations use; must name a file that exists in <see cref="DirectoryPath"/>.</summary>
	public string? CurrentVersion { get; set; }
}
