using System.Security.Cryptography;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Sessions;
using Services.Sessions.Service;
using Services.Sync.Contracts;
using Xunit;
using WireContracts = Services.Sync.Contracts;

namespace RecoveryTests;

/// <summary>
/// The server-carried approval: a trusted device records a grant, and the requesting device's own
/// registration consumes it. The grant can never authorize anything other than the exact request its
/// approver saw.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ApprovedRegistrationTests(DatabaseFixture fixture)
{
	private sealed record Scenario(
		SyncAccountEntity Account,
		SyncDeviceRegistrationEntity Approver,
		SyncDeviceSessionEntity ApproverSession,
		ECDsa ApproverKey,
		string PendingDeviceId,
		ECDsa PendingKey,
		string LocalInstanceId,
		WireContracts.DeviceChallenge Challenge);

	private async Task<Scenario> SeedApprovedRequestAsync(AppDbContext db, RecoveryTestContext ctx, bool approve = true)
	{
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		var (approverSpki, approverKey) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity approver = await TestData.CreateTrustedDeviceAsync(db, account, approverSpki, "key-approver");
		SyncLocalInstanceEntity approverInstance = await TestData.CreateLocalInstanceAsync(db, approver);
		SyncDeviceSessionEntity approverSession = await TestData.CreateSessionAsync(db, account, approver, approverInstance, "raw-token-approver");

		var (pendingSpki, pendingKey) = TestData.NewDeviceKey();
		string pendingDeviceId = TestData.NewId("dev-pending");
		string localInstanceId = TestData.NewId("inst-pending");
		WireContracts.DeviceChallenge challenge = await ctx.Challenges.IssueEnrollmentChallengeAsync(
			db, account.Id, pendingDeviceId, "key-pending", pendingSpki, localInstanceId, Database.Enums.Sync.ChallengePurpose.DeviceApproval);

		if (approve)
		{
			await ctx.Approvals.ApproveAsync(
				db, account.Id, approver.Id, approverSession.Id,
				new ApproveDeviceRequest(challenge.ChallengeId, RecoveryTestContext.Sign("chat-device-approval-v1", challenge, approverKey).Signature, null),
				ctx.Clock.GetUtcNow().UtcDateTime);
		}

		return new Scenario(account, approver, approverSession, approverKey, pendingDeviceId, pendingKey, localInstanceId, challenge);
	}

	[Fact]
	public async Task RecordedApproval_IsConsumedExactlyOnce_AndTrustsTheRequestingDevice()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedApprovedRequestAsync(db, ctx);
		DeviceEnrollmentCoordinator coordinator = ctx.Enrollment();

		SessionTokens tokens = await coordinator.RegisterDeviceAsync(
			db, scenario.Account.LegacyUserId, scenario.LocalInstanceId,
			RecoveryTestContext.Sign("chat-device-proof-v1", scenario.Challenge, scenario.PendingKey), approval: null);

		Assert.Equal(scenario.PendingDeviceId, tokens.Session.Device.DeviceRegistrationId);

		await using AppDbContext verifyDb = fixture.CreateContext();
		SyncDeviceRegistrationEntity device = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == scenario.PendingDeviceId);
		Assert.Equal(DeviceTrustState.Trusted, device.TrustState);
		Assert.Equal(scenario.Approver.Id, device.ApprovedByDeviceRegistrationId);
		SyncDeviceApprovalGrantEntity grant = await verifyDb.SyncDeviceApprovalGrants.SingleAsync(g => g.ChallengeId == scenario.Challenge.ChallengeId);
		Assert.NotNull(grant.ConsumedAtUtc);
	}

	[Fact]
	public async Task AnApprovedRequest_CannotBeRegisteredWithADifferentKey()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedApprovedRequestAsync(db, ctx);

		// The requester tries to register the approved challenge with a key the approver never saw. The
		// challenge row holds the approved key, so the device proof itself fails first.
		using ECDsa replacementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		await Assert.ThrowsAsync<DeviceProofInvalidException>(() => ctx.Enrollment().RegisterDeviceAsync(
			db, scenario.Account.LegacyUserId, scenario.LocalInstanceId,
			RecoveryTestContext.Sign("chat-device-proof-v1", scenario.Challenge, replacementKey), approval: null));

		await using AppDbContext verifyDb = fixture.CreateContext();
		Assert.Null((await verifyDb.SyncDeviceApprovalGrants.SingleAsync(g => g.ChallengeId == scenario.Challenge.ChallengeId)).ConsumedAtUtc);
	}

	[Fact]
	public async Task AnApprovedRequest_CannotBeRegisteredForADifferentLocalInstance()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedApprovedRequestAsync(db, ctx);

		await Assert.ThrowsAsync<ChallengeInvalidException>(() => ctx.Enrollment().RegisterDeviceAsync(
			db, scenario.Account.LegacyUserId, TestData.NewId("other-inst"),
			RecoveryTestContext.Sign("chat-device-proof-v1", scenario.Challenge, scenario.PendingKey), approval: null));
	}

	[Fact]
	public async Task AnApproverRevokedBeforeTheRegistrationCommits_InvalidatesTheGrant()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedApprovedRequestAsync(db, ctx);

		await using (AppDbContext revokeDb = fixture.CreateContext())
		{
			await ctx.Trust.RevokeDeviceAsync(
				revokeDb, scenario.Account.Id, scenario.Approver.Id, scenario.Approver.TrustVersion, scenario.Approver.Id,
				ctx.Clock.GetUtcNow().UtcDateTime, scenario.ApproverSession.Id);
		}

		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() => ctx.Enrollment().RegisterDeviceAsync(
			db, scenario.Account.LegacyUserId, scenario.LocalInstanceId,
			RecoveryTestContext.Sign("chat-device-proof-v1", scenario.Challenge, scenario.PendingKey), approval: null));
	}

	[Fact]
	public async Task AnApproverWhoseSessionEndedBeforeCommit_InvalidatesTheGrant()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedApprovedRequestAsync(db, ctx);

		await db.Database.ExecuteSqlInterpolatedAsync(
			$"""UPDATE "SyncDeviceSessions" SET "EndedAtUtc" = {ctx.Clock.GetUtcNow().UtcDateTime}, "CurrentRefreshTokenHash" = NULL, "RefreshTokenExpiresAtUtc" = NULL WHERE "Id" = {scenario.ApproverSession.Id}""");

		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() => ctx.Enrollment().RegisterDeviceAsync(
			db, scenario.Account.LegacyUserId, scenario.LocalInstanceId,
			RecoveryTestContext.Sign("chat-device-proof-v1", scenario.Challenge, scenario.PendingKey), approval: null));
	}

	[Fact]
	public async Task WithoutAnyApproval_TheRequestingDeviceStaysPending()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedApprovedRequestAsync(db, ctx, approve: false);

		await Assert.ThrowsAsync<DeviceApprovalRequiredException>(() => ctx.Enrollment().RegisterDeviceAsync(
			db, scenario.Account.LegacyUserId, scenario.LocalInstanceId,
			RecoveryTestContext.Sign("chat-device-proof-v1", scenario.Challenge, scenario.PendingKey), approval: null));

		await using AppDbContext verifyDb = fixture.CreateContext();
		SyncDeviceRegistrationEntity device = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == scenario.PendingDeviceId);
		Assert.Equal(DeviceTrustState.Pending, device.TrustState);
	}

	[Fact]
	public async Task ReplayingTheSameRegistration_CannotReuseTheConsumedChallengeOrGrant()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedApprovedRequestAsync(db, ctx);
		DeviceEnrollmentCoordinator coordinator = ctx.Enrollment();
		WireContracts.DeviceProof proof = RecoveryTestContext.Sign("chat-device-proof-v1", scenario.Challenge, scenario.PendingKey);

		await coordinator.RegisterDeviceAsync(db, scenario.Account.LegacyUserId, scenario.LocalInstanceId, proof, approval: null);

		await Assert.ThrowsAsync<ChallengeInvalidException>(() =>
			coordinator.RegisterDeviceAsync(db, scenario.Account.LegacyUserId, scenario.LocalInstanceId, proof, approval: null));
	}
}
