using Microsoft.Extensions.Options;
using Services.Keys;
using Services.Keys.Service;
using Xunit;

namespace KeyModuleTests;

public class FileMasterSecretProviderTests
{
	[Fact]
	public void Construction_Succeeds_WhenCurrentVersionIsReadable()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var provider = new FileMasterSecretProvider(dir.OptionsFor("v1"));
		Assert.Equal("v1", provider.CurrentVersion);
	}

	[Fact]
	public void GetKey_ReturnsTheExactBytesWritten()
	{
		using var dir = new TestMasterSecretDirectory();
		byte[] written = dir.WriteVersion("v1");
		var provider = new FileMasterSecretProvider(dir.OptionsFor("v1"));
		Assert.Equal(written, provider.GetKey("v1"));
	}

	[Fact]
	public void GetKey_CanReadAnOlderVersionBesidesCurrent_ForRewrap()
	{
		using var dir = new TestMasterSecretDirectory();
		byte[] oldKey = dir.WriteVersion("v1");
		dir.WriteVersion("v2");
		var provider = new FileMasterSecretProvider(dir.OptionsFor("v2"));
		Assert.Equal("v2", provider.CurrentVersion);
		Assert.Equal(oldKey, provider.GetKey("v1"));
	}

	[Fact]
	public void Construction_Throws_WhenDirectoryMissing()
	{
		var options = Options.Create(new MasterSecretOptions { DirectoryPath = "/nonexistent/path/xyz", CurrentVersion = "v1" });
		Assert.Throws<MasterSecretUnavailableException>(() => new FileMasterSecretProvider(options));
	}

	[Fact]
	public void Construction_Throws_WhenDirectoryPathNotConfigured()
	{
		var options = Options.Create(new MasterSecretOptions { DirectoryPath = null, CurrentVersion = "v1" });
		Assert.Throws<MasterSecretUnavailableException>(() => new FileMasterSecretProvider(options));
	}

	[Fact]
	public void Construction_Throws_WhenCurrentVersionNotConfigured()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var options = Options.Create(new MasterSecretOptions { DirectoryPath = dir.Path, CurrentVersion = null });
		Assert.Throws<MasterSecretUnavailableException>(() => new FileMasterSecretProvider(options));
	}

	[Fact]
	public void Construction_Throws_WhenCurrentVersionFileMissing()
	{
		using var dir = new TestMasterSecretDirectory();
		var options = dir.OptionsFor("v1"); // never written
		Assert.Throws<MasterSecretUnavailableException>(() => new FileMasterSecretProvider(options));
	}

	[Fact]
	public void Construction_Throws_WhenKeyFileIsWrongLength()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1", new byte[16]); // wrong length
		Assert.Throws<MasterSecretUnavailableException>(() => new FileMasterSecretProvider(dir.OptionsFor("v1")));
	}

	[Fact]
	public void GetKey_Throws_ForUnknownVersion()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var provider = new FileMasterSecretProvider(dir.OptionsFor("v1"));
		Assert.Throws<MasterSecretUnavailableException>(() => provider.GetKey("v99"));
	}

	[Theory]
	[InlineData("../escape")]
	[InlineData("v1/../v2")]
	[InlineData("v1 ")]
	[InlineData("")]
	public void GetKey_RejectsVersionStringsThatAreNotSafeFilenames(string version)
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var provider = new FileMasterSecretProvider(dir.OptionsFor("v1"));
		Assert.Throws<MasterSecretUnavailableException>(() => provider.GetKey(version));
	}

	[Fact]
	public void GetKey_Throws_AfterVersionFileIsRemoved()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		dir.WriteVersion("v2");
		var provider = new FileMasterSecretProvider(dir.OptionsFor("v1"));
		dir.DeleteVersion("v2");
		Assert.Throws<MasterSecretUnavailableException>(() => provider.GetKey("v2"));
	}

	[Fact]
	public void GetKey_ReturnsADistinctArrayInstance_EveryCall_SoCallerMutationCannotCorruptTheCache()
	{
		// Regression test for HIGH #4 (review-report.md): GetKey previously returned the exact same
		// cached array instance on every call, so a caller zeroing "its own" key (a reasonable-
		// looking key-hygiene habit) permanently corrupted the master key for every subsequent
		// Protect/Unprotect call, server-wide, until process restart.
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var provider = new FileMasterSecretProvider(dir.OptionsFor("v1"));

		byte[] firstCall = provider.GetKey("v1");
		byte[] secondCallBeforeMutation = provider.GetKey("v1");
		Assert.False(ReferenceEquals(firstCall, secondCallBeforeMutation));
		Assert.Equal(firstCall, secondCallBeforeMutation);

		Array.Clear(firstCall);

		byte[] thirdCall = provider.GetKey("v1");
		Assert.NotEqual(firstCall, thirdCall); // firstCall is now all-zero; thirdCall must not be.
		Assert.Equal(secondCallBeforeMutation, thirdCall); // the real cached key is untouched.
	}

	[Fact]
	public void ExceptionMessages_NeverContainKeyBytes()
	{
		using var dir = new TestMasterSecretDirectory();
		byte[] written = dir.WriteVersion("v1", new byte[16]); // wrong length, triggers a message
		string hex = Convert.ToHexString(written);
		try
		{
			_ = new FileMasterSecretProvider(dir.OptionsFor("v1"));
			Assert.Fail("Expected MasterSecretUnavailableException.");
		}
		catch (MasterSecretUnavailableException ex)
		{
			Assert.DoesNotContain(hex, ex.ToString());
			Assert.DoesNotContain(Convert.ToBase64String(written), ex.ToString());
		}
	}
}
