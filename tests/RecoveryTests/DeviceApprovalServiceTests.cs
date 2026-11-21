using System.Security.Cryptography;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sync.Contracts;
using Xunit;
using WireContracts = Services.Sync.Contracts;

namespace RecoveryTests;

/// <summary>
/// Approval from another trusted device: bound to the exact pending request, one-use, re-validated at
/// commit, and carrying the author-key handoff that makes approval alone restore authorship.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DeviceApprovalServiceTests(DatabaseFixture fixture)
{
	private sealed record Scenario(
		SyncAccountEntity Account,
		SyncDeviceRegistrationEntity Approver,
		SyncDeviceSessionEntity ApproverSession,
		ECDsa ApproverKey,
		string PendingDeviceId,
		ECDsa PendingKey,
		WireContracts.DeviceChallenge Challenge);

	private async Task<Scenario> SeedPendingApprovalAsync(AppDbContext db, RecoveryTestContext ctx)
	{
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		var (approverSpki, approverKey) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity approver = await TestData.CreateTrustedDeviceAsync(db, account, approverSpki, "key-approver");
		SyncLocalInstanceEntity approverInstance = await TestData.CreateLocalInstanceAsync(db, approver);
		SyncDeviceSessionEntity approverSession = await TestData.CreateSessionAsync(db, account, approver, approverInstance, "raw-token-approver");

		var (pendingSpki, pendingKey) = TestData.NewDeviceKey();
		string pendingDeviceId = TestData.NewId("dev-pending");
		WireContracts.DeviceChallenge challenge = await ctx.Challenges.IssueEnrollmentChallengeAsync(
			db, account.Id, pendingDeviceId, "key-pending", pendingSpki, TestData.NewId("inst-pending"), Database.Enums.Sync.ChallengePurpose.DeviceApproval);

		return new Scenario(account, approver, approverSession, approverKey, pendingDeviceId, pendingKey, challenge);
	}

	private static ApproveDeviceRequest ApprovalFor(Scenario scenario, ECDsa signingKey, AuthorKeyHandoff? handoff = null) =>
		new(scenario.Challenge.ChallengeId,
			RecoveryTestContext.Sign("chat-device-approval-v1", scenario.Challenge, signingKey).Signature,
			handoff);

	private static AuthorKeyHandoff SomeHandoff() =>
		new(Base64Url.Encode(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo()),
			new EncryptedContent(
				Base64Url.Encode(RandomNumberGenerator.GetBytes(12)),
				Base64Url.Encode(RandomNumberGenerator.GetBytes(48)),
				Base64Url.Encode(RandomNumberGenerator.GetBytes(16))));

	[Fact]
	public async Task PendingList_ShowsExactlyWhatTheApprovalSignatureCommitsTo()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);

		IReadOnlyList<PendingApprovalRequest> pending = await ctx.Approvals.ListPendingAsync(db, scenario.Account.Id, ctx.Clock.GetUtcNow().UtcDateTime);

		PendingApprovalRequest request = Assert.Single(pending);
		Assert.Equal(scenario.Challenge.ChallengeId, request.ChallengeId);
		Assert.Equal(scenario.PendingDeviceId, request.DeviceRegistrationId);
		Assert.Equal(scenario.Challenge.PublicKey.Spki, request.PublicKey.Spki);
		Assert.Equal(scenario.Challenge.Nonce, request.Nonce);
		Assert.Equal(scenario.Challenge.ExpiresAtUtc, request.ExpiresAtUtc);
		Assert.False(string.IsNullOrEmpty(request.LocalInstanceId));
	}

	[Fact]
	public async Task Approve_RecordsTheGrantAndIsIdempotentForTheSameApprover()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);
		ApproveDeviceRequest request = ApprovalFor(scenario, scenario.ApproverKey, SomeHandoff());

		ApproveDeviceResult first = await ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id, request, ctx.Clock.GetUtcNow().UtcDateTime);
		ApproveDeviceResult second = await ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id, request, ctx.Clock.GetUtcNow().UtcDateTime);

		Assert.Equal(DeviceApprovalOutcome.Recorded, first.Outcome);
		Assert.Equal(DeviceApprovalOutcome.AlreadyApproved, second.Outcome);

		await using AppDbContext verifyDb = fixture.CreateContext();
		SyncDeviceApprovalGrantEntity grant = await verifyDb.SyncDeviceApprovalGrants.SingleAsync(g => g.ChallengeId == scenario.Challenge.ChallengeId);
		Assert.Equal(scenario.PendingDeviceId, grant.PendingDeviceRegistrationId);
		Assert.Equal("key-pending", grant.PendingKeyId);
		Assert.Equal(scenario.Approver.Id, grant.ApprovingDeviceRegistrationId);
		Assert.Null(grant.ConsumedAtUtc);
		// Exactly one handoff, for the device the approval was granted to.
		SyncAuthorKeyHandoffEntity handoff = await verifyDb.SyncAuthorKeyHandoffs.SingleAsync(h => h.ChallengeId == scenario.Challenge.ChallengeId);
		Assert.Equal(scenario.PendingDeviceId, handoff.PendingDeviceRegistrationId);
		Assert.Null(handoff.DeliveredAtUtc);
	}

	[Fact]
	public async Task Approve_RejectsASignatureFromAnotherKey()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);
		using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

		await Assert.ThrowsAsync<DeviceProofInvalidException>(() => ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id, ApprovalFor(scenario, attacker), ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Fact]
	public async Task Approve_RejectsAnExpiredRequest()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);

		DateTime afterExpiry = DateTime.Parse(scenario.Challenge.ExpiresAtUtc).ToUniversalTime().AddSeconds(1);

		await Assert.ThrowsAsync<ApprovalRequestInvalidException>(() => ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id, ApprovalFor(scenario, scenario.ApproverKey), afterExpiry));
		Assert.Empty(await ctx.Approvals.ListPendingAsync(db, scenario.Account.Id, afterExpiry));
	}

	[Fact]
	public async Task Approve_RefusesSelfApproval()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);

		// The pending device asking to approve its own request, with its own key.
		await Assert.ThrowsAsync<ApprovalRequestInvalidException>(() => ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.PendingDeviceId, scenario.ApproverSession.Id,
			ApprovalFor(scenario, scenario.PendingKey), ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Fact]
	public async Task Approve_RefusesAnApproverWhoseSessionHasEnded()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);

		await db.Database.ExecuteSqlInterpolatedAsync(
			$"""UPDATE "SyncDeviceSessions" SET "EndedAtUtc" = {ctx.Clock.GetUtcNow().UtcDateTime}, "CurrentRefreshTokenHash" = NULL, "RefreshTokenExpiresAtUtc" = NULL WHERE "Id" = {scenario.ApproverSession.Id}""");

		await Assert.ThrowsAsync<SessionNotTrustedException>(() => ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id,
			ApprovalFor(scenario, scenario.ApproverKey), ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Fact]
	public async Task Approve_RefusesARevokedApprover()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);

		await using (AppDbContext revokeDb = fixture.CreateContext())
		{
			await ctx.Trust.RevokeDeviceAsync(
				revokeDb, scenario.Account.Id, scenario.Approver.Id, scenario.Approver.TrustVersion, scenario.Approver.Id,
				ctx.Clock.GetUtcNow().UtcDateTime, scenario.ApproverSession.Id);
		}

		await Assert.ThrowsAsync<SessionNotTrustedException>(() => ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id,
			ApprovalFor(scenario, scenario.ApproverKey), ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Fact]
	public async Task ConcurrentApprovals_ResolveToOneGrant()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext seedDb = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(seedDb, ctx);
		ApproveDeviceRequest request = ApprovalFor(scenario, scenario.ApproverKey);

		async Task<ApproveDeviceResult> ApproveOnOwnConnectionAsync()
		{
			await using AppDbContext db = fixture.CreateContext();
			return await ctx.Approvals.ApproveAsync(
				db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id, request, ctx.Clock.GetUtcNow().UtcDateTime);
		}

		ApproveDeviceResult[] results = await Task.WhenAll(ApproveOnOwnConnectionAsync(), ApproveOnOwnConnectionAsync());

		await using AppDbContext verifyDb = fixture.CreateContext();
		Assert.Equal(1, await verifyDb.SyncDeviceApprovalGrants.CountAsync(g => g.ChallengeId == scenario.Challenge.ChallengeId));
		Assert.Contains(results, r => r.Outcome == DeviceApprovalOutcome.Recorded);
	}

	[Fact]
	public async Task Handoff_StaysAvailableUntilTheDeviceAcknowledgesIt()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);
		await ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id,
			ApprovalFor(scenario, scenario.ApproverKey, SomeHandoff()), ctx.Clock.GetUtcNow().UtcDateTime);

		// A fetch whose response is lost, then a retry: the material must still be there.
		AuthorKeyHandoffView? first = await ctx.Approvals.GetPendingHandoffAsync(db, scenario.Account.Id, scenario.PendingDeviceId);
		AuthorKeyHandoffView? retry = await ctx.Approvals.GetPendingHandoffAsync(db, scenario.Account.Id, scenario.PendingDeviceId);
		Assert.NotNull(first);
		Assert.Equal(first!.Content.Ciphertext, retry!.Content.Ciphertext);

		await ctx.Approvals.AcknowledgeHandoffAsync(db, scenario.Account.Id, scenario.PendingDeviceId, first.ChallengeId, ctx.Clock.GetUtcNow().UtcDateTime);

		Assert.Null(await ctx.Approvals.GetPendingHandoffAsync(db, scenario.Account.Id, scenario.PendingDeviceId));
		// A repeated acknowledgement is harmless.
		await ctx.Approvals.AcknowledgeHandoffAsync(db, scenario.Account.Id, scenario.PendingDeviceId, first.ChallengeId, ctx.Clock.GetUtcNow().UtcDateTime);
	}

	[Fact]
	public async Task Handoff_IsNeverDeliveredToAnotherDevice()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		Scenario scenario = await SeedPendingApprovalAsync(db, ctx);
		await ctx.Approvals.ApproveAsync(
			db, scenario.Account.Id, scenario.Approver.Id, scenario.ApproverSession.Id,
			ApprovalFor(scenario, scenario.ApproverKey, SomeHandoff()), ctx.Clock.GetUtcNow().UtcDateTime);

		Assert.Null(await ctx.Approvals.GetPendingHandoffAsync(db, scenario.Account.Id, scenario.Approver.Id));
		// An acknowledgement from the wrong device cannot burn it either.
		await ctx.Approvals.AcknowledgeHandoffAsync(db, scenario.Account.Id, scenario.Approver.Id, scenario.Challenge.ChallengeId, ctx.Clock.GetUtcNow().UtcDateTime);
		Assert.NotNull(await ctx.Approvals.GetPendingHandoffAsync(db, scenario.Account.Id, scenario.PendingDeviceId));
	}
}
