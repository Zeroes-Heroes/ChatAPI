using System.Text;
using Services.Keys;
using Services.Keys.Service;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Xunit;

namespace SessionTests;

public class AesGcmReplayPayloadProtectorTests
{
	private static AesGcmReplayPayloadProtector MakeProtector(TestMasterSecretDirectory dir, string version = "v1")
	{
		dir.WriteVersion(version);
		return new AesGcmReplayPayloadProtector(new FileMasterSecretProvider(dir.OptionsFor(version)));
	}

	[Fact]
	public void ProtectThenUnprotect_RoundTrips()
	{
		using var dir = new TestMasterSecretDirectory();
		var protector = MakeProtector(dir);
		ReplayProtectionContext context = new("acct-1", "sess-1", "op-1");
		byte[] plaintext = Encoding.UTF8.GetBytes("{\"accessToken\":\"example\"}");

		ProtectedReplayPayload protectedPayload = protector.Protect(plaintext, context);
		byte[] recovered = protector.Unprotect(protectedPayload, context);

		Assert.Equal(plaintext, recovered);
		Assert.NotEqual(plaintext, protectedPayload.Ciphertext);
	}

	[Theory]
	[InlineData("acct-2", "sess-1", "op-1")]
	[InlineData("acct-1", "sess-2", "op-1")]
	[InlineData("acct-1", "sess-1", "op-2")]
	public void Unprotect_RejectsAnyContextSubstitution(string accountId, string sessionId, string operationId)
	{
		using var dir = new TestMasterSecretDirectory();
		var protector = MakeProtector(dir);
		ReplayProtectionContext original = new("acct-1", "sess-1", "op-1");
		byte[] plaintext = Encoding.UTF8.GetBytes("payload");
		ProtectedReplayPayload protectedPayload = protector.Protect(plaintext, original);

		ReplayProtectionContext wrong = new(accountId, sessionId, operationId);
		Assert.Throws<KeyProtectionIntegrityException>(() => protector.Unprotect(protectedPayload, wrong));
	}

	[Fact]
	public void Unprotect_RejectsTamperedCiphertext()
	{
		using var dir = new TestMasterSecretDirectory();
		var protector = MakeProtector(dir);
		ReplayProtectionContext context = new("acct-1", "sess-1", "op-1");
		ProtectedReplayPayload protectedPayload = protector.Protect(Encoding.UTF8.GetBytes("payload"), context);

		byte[] tamperedCiphertext = (byte[])protectedPayload.Ciphertext.Clone();
		tamperedCiphertext[0] ^= 0xFF;
		ProtectedReplayPayload tampered = protectedPayload with { Ciphertext = tamperedCiphertext };

		Assert.Throws<KeyProtectionIntegrityException>(() => protector.Unprotect(tampered, context));
	}

	[Fact]
	public void DbDumpAlone_WithoutTheMasterSecret_CannotRecoverThePlaintext()
	{
		using var dir = new TestMasterSecretDirectory();
		var protector = MakeProtector(dir);
		ReplayProtectionContext context = new("acct-1", "sess-1", "op-1");
		byte[] plaintext = Encoding.UTF8.GetBytes("super-secret-tokens");
		ProtectedReplayPayload protectedPayload = protector.Protect(plaintext, context);

		using var wrongSecretDir = new TestMasterSecretDirectory();
		wrongSecretDir.WriteVersion(protectedPayload.MasterKeyVersion);
		var attacker = new AesGcmReplayPayloadProtector(new FileMasterSecretProvider(wrongSecretDir.OptionsFor(protectedPayload.MasterKeyVersion)));

		Assert.Throws<KeyProtectionIntegrityException>(() => attacker.Unprotect(protectedPayload, context));
	}
}
