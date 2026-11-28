using System.Security.Cryptography;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums.Sync;

namespace RecoveryTests;

/// <summary>Minimal seeding helpers for this suite - a separate copy from ServerDataTests/KeyModuleTests's own TestData.cs, per this task's "keep inherited test projects untouched" convention.</summary>
internal static class TestData
{
	public static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

	public static async Task<SyncAccountEntity> CreateAccountAsync(AppDbContext db, CancellationToken ct = default)
	{
		UserEntity user = new($"Test {Guid.NewGuid():N}", $"+1555{Random.Shared.Next(1000000, 9999999)}");
		db.Users.Add(user);
		await db.SaveChangesAsync(ct);

		SyncAccountEntity account = new(NewId("acct"), user.Id, DateTime.UtcNow);
		db.SyncAccounts.Add(account);
		await db.SaveChangesAsync(ct);
		return account;
	}

	/// <summary>A real P-256 keypair, for device-proof/approval signature tests - never a placeholder key, since DeviceProofVerifier genuinely validates the curve/signature.</summary>
	public static (byte[] Spki, ECDsa Key) NewDeviceKey()
	{
		ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		return (key.ExportSubjectPublicKeyInfo(), key);
	}

	public static async Task<SyncDeviceRegistrationEntity> CreateTrustedDeviceAsync(
		AppDbContext db, SyncAccountEntity account, byte[] spki, string keyId, bool isFirstDevice = true, CancellationToken ct = default)
	{
		SyncDeviceRegistrationEntity device = new(NewId("dev"), account.Id, DateTime.UtcNow, keyId, spki, isFirstDevice, approvedByDeviceRegistrationId: null);
		device.TrustState = DeviceTrustState.Trusted;
		db.SyncDeviceRegistrations.Add(device);
		await db.SaveChangesAsync(ct);
		return device;
	}

	public static async Task<SyncDeviceRegistrationEntity> CreatePendingDeviceAsync(
		AppDbContext db, SyncAccountEntity account, byte[] spki, string keyId, CancellationToken ct = default)
	{
		SyncDeviceRegistrationEntity device = new(NewId("dev"), account.Id, DateTime.UtcNow, keyId, spki, isFirstDevice: false, approvedByDeviceRegistrationId: null);
		db.SyncDeviceRegistrations.Add(device);
		await db.SaveChangesAsync(ct);
		return device;
	}

	public static async Task<SyncLocalInstanceEntity> CreateLocalInstanceAsync(AppDbContext db, SyncDeviceRegistrationEntity device, CancellationToken ct = default)
	{
		SyncLocalInstanceEntity instance = new(NewId("inst"), device.Id, DateTime.UtcNow);
		db.SyncLocalInstances.Add(instance);
		await db.SaveChangesAsync(ct);
		return instance;
	}

	public static async Task<SyncDeviceSessionEntity> CreateSessionAsync(
		AppDbContext db, SyncAccountEntity account, SyncDeviceRegistrationEntity device, SyncLocalInstanceEntity instance,
		string rawRefreshToken, DateTime? refreshExpiresAtUtc = null, CancellationToken ct = default)
	{
		byte[] hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(rawRefreshToken));
		SyncDeviceSessionEntity session = new(
			NewId("sess"), account.Id, device.Id, instance.Id, DateTime.UtcNow, device.TrustVersion, hash,
			refreshExpiresAtUtc ?? DateTime.UtcNow.AddDays(30));
		db.SyncDeviceSessions.Add(session);
		await db.SaveChangesAsync(ct);
		return session;
	}
}
