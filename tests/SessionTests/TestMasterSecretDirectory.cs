using Microsoft.Extensions.Options;
using Services.Keys;

namespace SessionTests;

/// <summary>Copy of KeyModuleTests/TestMasterSecretDirectory.cs's own pattern - a separate copy so that inherited test project stays untouched, per this task's own convention.</summary>
internal sealed class TestMasterSecretDirectory : IDisposable
{
	public string Path { get; }

	public TestMasterSecretDirectory()
	{
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chatapp-sessiontests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path);
	}

	public byte[] WriteVersion(string version, byte[]? bytes = null)
	{
		byte[] keyBytes = bytes ?? RandomBytes();
		File.WriteAllBytes(System.IO.Path.Combine(Path, $"{version}.key"), keyBytes);
		return keyBytes;
	}

	public IOptions<MasterSecretOptions> OptionsFor(string currentVersion) =>
		Options.Create(new MasterSecretOptions { DirectoryPath = Path, CurrentVersion = currentVersion });

	private static byte[] RandomBytes()
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
