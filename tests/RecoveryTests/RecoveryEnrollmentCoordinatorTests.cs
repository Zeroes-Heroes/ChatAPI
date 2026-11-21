using System.Security.Claims;
using System.Security.Cryptography;
using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Recovery;
using Services.Recovery.Interface;
using Services.Sessions;
using Services.Sync.Contracts;
using Xunit;
using WireContracts = Services.Sync.Contracts;

namespace RecoveryTests;

/// <summary>
/// The recovery-secret alternative (MESSAGE_SYNC_ANALYSIS.md:153): a device with no session proves it
/// opened the envelope, and that proof - not a client flag - authorizes trust, unless the sticky
/// revocation barrier is up.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RecoveryEnrollmentCoordinatorTests(DatabaseFixture fixture)
{
	/// <summary>Seeds one trusted device with a stored envelope, as a real account that set recovery up would have.</summary>
	private async Task<(SyncAccountEntity Account, SyncDeviceRegistrationEntity Device, SyncDeviceSessionEntity Session, ECDsa RecoveryKey)>
		SeedAccountWithEnvelopeAsync(AppDbContext db, RecoveryTestContext ctx)
	{
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		SyncLocalInstanceEntity instance = await TestData.CreateLocalInstanceAsync(db, device);
		SyncDeviceSessionEntity session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");
		ClaimsPrincipal caller = ctx.AppSessionPrincipal(account.LegacyUserId, account.Id, session.Id, device.Id, instance.Id, device.TrustVersion);

		var (request, _, recoveryKey) = RecoveryTestContext.BuildEnvelope(account.Id, "1");
		await ctx.Envelopes(db).StoreAsync(db, caller, request, ctx.Clock.GetUtcNow().UtcDateTime);
		await ctx.Envelopes(db).ConfirmAsync(db, caller, "1", ctx.Clock.GetUtcNow().UtcDateTime);
		return (account, device, session, recoveryKey);
	}

	[Fact]
	public async Task ValidSecret_OnANewDevice_RecoversTheOldContext_WhenNoRevocationHasHappened()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, recoveryKey) = await SeedAccountWithEnvelopeAsync(db, ctx);
		IRecoveryEnrollmentCoordinator coordinator = ctx.RecoveryEnrollment(db);

		var (newSpki, newKey) = TestData.NewDeviceKey();
		string newDeviceId = TestData.NewId("dev");
		string localInstanceId = TestData.NewId("inst");

		// A trusted device still exists and is unreachable to the user; the secret is the alternative.
		RecoveryBootstrapChallenge bootstrap = await coordinator.RequestRecoveryChallengeAsync(
			db, account.LegacyUserId, newDeviceId, "key-new", newSpki, localInstanceId);
		Assert.Equal(WireContracts.ChallengePurpose.Recovery, bootstrap.Challenge.Purpose);
		Assert.Single(bootstrap.Candidates);

		SessionTokens tokens = await coordinator.RegisterWithRecoveryAsync(db, account.LegacyUserId, new RecoveryRegisterRequest(
			localInstanceId,
			RecoveryTestContext.Sign("chat-device-proof-v1", bootstrap.Challenge, newKey),
			bootstrap.Candidates[0].Version,
			RecoveryTestContext.Sign("chat-recovery-proof-v1", bootstrap.Challenge, recoveryKey)));

		Assert.Equal(newDeviceId, tokens.Session.Device.DeviceRegistrationId);
		Assert.Equal(account.Id, tokens.Session.Device.AccountContextId);

		await using AppDbContext verifyDb = fixture.CreateContext();
		SyncDeviceRegistrationEntity recovered = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == newDeviceId);
		Assert.Equal(Database.Enums.Sync.DeviceTrustState.Trusted, recovered.TrustState);
		Assert.False(recovered.IsFirstDevice);
		Assert.Null(recovered.ApprovedByDeviceRegistrationId);
	}

	[Fact]
	public async Task AfterRevocation_TheSealedPackageIsNotEvenReleased_AndRegistrationIsBlocked()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, device, session, recoveryKey) = await SeedAccountWithEnvelopeAsync(db, ctx);
		IRecoveryEnrollmentCoordinator coordinator = ctx.RecoveryEnrollment(db);

		var (newSpki, newKey) = TestData.NewDeviceKey();
		string newDeviceId = TestData.NewId("dev");
		string localInstanceId = TestData.NewId("inst");

		// A challenge obtained BEFORE the revocation, to prove the commit-time re-check too.
		RecoveryBootstrapChallenge beforeBarrier = await coordinator.RequestRecoveryChallengeAsync(
			db, account.LegacyUserId, newDeviceId, "key-new", newSpki, localInstanceId);

		await using (AppDbContext revokeDb = fixture.CreateContext())
		{
			await ctx.Trust.RevokeDeviceAsync(revokeDb, account.Id, device.Id, device.TrustVersion, device.Id, ctx.Clock.GetUtcNow().UtcDateTime, session.Id);
		}

		// 1. The authorship material is not released at all any more.
		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			coordinator.RequestRecoveryChallengeAsync(db, account.LegacyUserId, TestData.NewId("dev"), "key-other", newSpki, TestData.NewId("inst")));

		// 2. Even the correct secret on an already-issued challenge cannot buy a session.
		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
			coordinator.RegisterWithRecoveryAsync(db, account.LegacyUserId, new RecoveryRegisterRequest(
				localInstanceId,
				RecoveryTestContext.Sign("chat-device-proof-v1", beforeBarrier.Challenge, newKey),
				beforeBarrier.Candidates[0].Version,
				RecoveryTestContext.Sign("chat-recovery-proof-v1", beforeBarrier.Challenge, recoveryKey))));
	}

	[Fact]
	public async Task RegeneratingDeviceIdentity_DoesNotEscapeTheBarrier()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, device, session, _) = await SeedAccountWithEnvelopeAsync(db, ctx);
		await using (AppDbContext revokeDb = fixture.CreateContext())
		{
			await ctx.Trust.RevokeDeviceAsync(revokeDb, account.Id, device.Id, device.TrustVersion, device.Id, ctx.Clock.GetUtcNow().UtcDateTime, session.Id);
		}

		// Three brand-new, never-seen registrations/keys: the barrier is account-scoped, so a fresh
		// identity is exactly as blocked as the revoked one.
		for (int attempt = 0; attempt < 3; attempt++)
		{
			var (spki, _) = TestData.NewDeviceKey();
			await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() =>
				ctx.RecoveryEnrollment(db).RequestRecoveryChallengeAsync(
					db, account.LegacyUserId, TestData.NewId("dev"), $"key-{attempt}", spki, TestData.NewId("inst")));
		}
	}

	[Fact]
	public async Task WrongRecoveryKey_IsRefused()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, _) = await SeedAccountWithEnvelopeAsync(db, ctx);
		IRecoveryEnrollmentCoordinator coordinator = ctx.RecoveryEnrollment(db);
		var (newSpki, newKey) = TestData.NewDeviceKey();
		string localInstanceId = TestData.NewId("inst");

		RecoveryBootstrapChallenge bootstrap = await coordinator.RequestRecoveryChallengeAsync(
			db, account.LegacyUserId, TestData.NewId("dev"), "key-new", newSpki, localInstanceId);
		using ECDsa attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

		await Assert.ThrowsAsync<DeviceProofInvalidException>(() =>
			coordinator.RegisterWithRecoveryAsync(db, account.LegacyUserId, new RecoveryRegisterRequest(
				localInstanceId,
				RecoveryTestContext.Sign("chat-device-proof-v1", bootstrap.Challenge, newKey),
				bootstrap.Candidates[0].Version,
				RecoveryTestContext.Sign("chat-recovery-proof-v1", bootstrap.Challenge, attackerKey))));
	}

	[Fact]
	public async Task ProofsForDifferentChallenges_AreRefused()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, recoveryKey) = await SeedAccountWithEnvelopeAsync(db, ctx);
		IRecoveryEnrollmentCoordinator coordinator = ctx.RecoveryEnrollment(db);
		var (newSpki, newKey) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");
		string localInstanceId = TestData.NewId("inst");

		RecoveryBootstrapChallenge first = await coordinator.RequestRecoveryChallengeAsync(db, account.LegacyUserId, deviceId, "key-new", newSpki, localInstanceId);
		RecoveryBootstrapChallenge second = await coordinator.RequestRecoveryChallengeAsync(db, account.LegacyUserId, deviceId, "key-new", newSpki, localInstanceId);

		await Assert.ThrowsAsync<DeviceProofInvalidException>(() =>
			coordinator.RegisterWithRecoveryAsync(db, account.LegacyUserId, new RecoveryRegisterRequest(
				localInstanceId,
				RecoveryTestContext.Sign("chat-device-proof-v1", second.Challenge, newKey),
				second.Candidates[0].Version,
				RecoveryTestContext.Sign("chat-recovery-proof-v1", first.Challenge, recoveryKey))));
	}

	[Fact]
	public async Task ADifferentLocalInstanceThanTheChallengeWasIssuedFor_IsRefused()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, recoveryKey) = await SeedAccountWithEnvelopeAsync(db, ctx);
		IRecoveryEnrollmentCoordinator coordinator = ctx.RecoveryEnrollment(db);
		var (newSpki, newKey) = TestData.NewDeviceKey();

		RecoveryBootstrapChallenge bootstrap = await coordinator.RequestRecoveryChallengeAsync(
			db, account.LegacyUserId, TestData.NewId("dev"), "key-new", newSpki, TestData.NewId("inst"));

		await Assert.ThrowsAsync<ChallengeInvalidException>(() =>
			coordinator.RegisterWithRecoveryAsync(db, account.LegacyUserId, new RecoveryRegisterRequest(
				TestData.NewId("other-inst"),
				RecoveryTestContext.Sign("chat-device-proof-v1", bootstrap.Challenge, newKey),
				bootstrap.Candidates[0].Version,
				RecoveryTestContext.Sign("chat-recovery-proof-v1", bootstrap.Challenge, recoveryKey))));
	}

	[Fact]
	public async Task AccountWithNoEnvelope_HasNothingToRecoverWith()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();

		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() =>
			ctx.RecoveryEnrollment(db).RequestRecoveryChallengeAsync(
				db, account.LegacyUserId, TestData.NewId("dev"), "key-new", spki, TestData.NewId("inst")));
	}

	[Fact]
	public async Task TheSameRecoveryChallenge_CannotBeUsedTwice()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, recoveryKey) = await SeedAccountWithEnvelopeAsync(db, ctx);
		IRecoveryEnrollmentCoordinator coordinator = ctx.RecoveryEnrollment(db);
		var (newSpki, newKey) = TestData.NewDeviceKey();
		string localInstanceId = TestData.NewId("inst");

		RecoveryBootstrapChallenge bootstrap = await coordinator.RequestRecoveryChallengeAsync(
			db, account.LegacyUserId, TestData.NewId("dev"), "key-new", newSpki, localInstanceId);
		RecoveryRegisterRequest request = new(
			localInstanceId,
			RecoveryTestContext.Sign("chat-device-proof-v1", bootstrap.Challenge, newKey),
			bootstrap.Candidates[0].Version,
			RecoveryTestContext.Sign("chat-recovery-proof-v1", bootstrap.Challenge, recoveryKey));

		await coordinator.RegisterWithRecoveryAsync(db, account.LegacyUserId, request);

		await Assert.ThrowsAsync<ChallengeInvalidException>(() =>
			coordinator.RegisterWithRecoveryAsync(db, account.LegacyUserId, request));
	}
}
