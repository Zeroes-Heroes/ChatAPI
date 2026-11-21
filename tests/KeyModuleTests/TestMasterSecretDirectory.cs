using Microsoft.Extensions.Options;
using Services.Keys;

namespace KeyModuleTests;

/// <summary>
/// A real temp directory standing in for the read-only mounted master-secret volume
/// docs/sync-contract-v1.md describes, with one "{version}.key" file per version (32 raw bytes
/// each) - exactly the shape `FileMasterSecretProvider` reads. Disposing deletes the directory;
/// tests that need to simulate "secret removed/rotated out" delete/rewrite a version file mid-test.
/// </summary>
internal sealed class TestMasterSecretDirectory : IDisposable
{
	public string Path { get; }

	public TestMasterSecretDirectory()
	{
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chatapp-keymodule-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path);
	}

	public byte[] WriteVersion(string version, byte[]? bytes = null)
	{
		byte[] keyBytes = bytes ?? RandomNumberGenerator();
		File.WriteAllBytes(System.IO.Path.Combine(Path, $"{version}.key"), keyBytes);
		return keyBytes;
	}

	public void DeleteVersion(string version) => File.Delete(System.IO.Path.Combine(Path, $"{version}.key"));

	public IOptions<MasterSecretOptions> OptionsFor(string currentVersion) =>
		Microsoft.Extensions.Options.Options.Create(new MasterSecretOptions { DirectoryPath = Path, CurrentVersion = currentVersion });

	private static byte[] RandomNumberGenerator()
	{
		byte[] bytes = new byte[32];
		System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
		return bytes;
	}

	public void Dispose()
	{
		if (Directory.Exists(Path))
		{
			Directory.Delete(Path, recursive: true);
		}
	}
}
