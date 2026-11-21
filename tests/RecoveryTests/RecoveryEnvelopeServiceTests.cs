using System.Security.Claims;
using System.Security.Cryptography;
using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Recovery;
using Services.Recovery.Interface;
using Services.Recovery.Models;
using Services.Sessions;
using Services.Sync.Contracts;
using Xunit;

namespace RecoveryTests;

/// <summary>
/// Storage and commit-time authority for the client-sealed recovery envelope. Runs against a real
/// PostgreSQL and the real key module - the envelope is never opened here, because the server cannot
/// open it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RecoveryEnvelopeServiceTests(DatabaseFixture fixture)
{
	private async Task<(SyncAccountEntity Account, SyncDeviceRegistrationEntity Device, SyncDeviceSessionEntity Session, ClaimsPrincipal Caller)>
		SeedTrustedCallerAsync(AppDbContext db, RecoveryTestContext ctx)
	{
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		SyncLocalInstanceEntity instance = await TestData.CreateLocalInstanceAsync(db, device);
		SyncDeviceSessionEntity session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");
		ClaimsPrincipal caller = ctx.AppSessionPrincipal(account.LegacyUserId, account.Id, session.Id, device.Id, instance.Id, device.TrustVersion);
		return (account, device, session, caller);
	}

	[Fact]
	public async Task StoredEnvelope_IsReturnedByteForByte_AndIsNotReadableFromTheDatabaseAlone()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		IRecoveryEnvelopeService service = ctx.Envelopes(db);
		var (request, _, _) = RecoveryTestContext.BuildEnvelope(account.Id, "1");

		await service.StoreAsync(db, caller, request, ctx.Clock.GetUtcNow().UtcDateTime);

		await using AppDbContext readDb = fixture.CreateContext();
		RecoveryEnvelopeView view = await ctx.Envelopes(readDb).GetCurrentAsync(readDb, account.Id);
		Assert.Null(view.ConfirmedVersion); // not confirmed until the device says the password was saved
		Assert.Equal("2", view.NextVersion);
		RecoveryPackage package = Assert.Single(view.Candidates);
		Assert.Equal(request.Content.Nonce, package.Content.Nonce);
		Assert.Equal(request.Content.Ciphertext, package.Content.Ciphertext);
		Assert.Equal(request.Content.Tag, package.Content.Tag);
		Assert.Equal(request.Kdf, package.Kdf);

		// A DB dump alone holds neither the client ciphertext nor anything openable without the mounted
		// master secret.
		SyncRecoveryEnvelopeEntity row = await readDb.SyncRecoveryEnvelopes.AsNoTracking().SingleAsync(e => e.AccountId == account.Id);
		Assert.NotEqual(RecoveryTestContext.DecodeBase64Url(request.Content.Ciphertext)!, row.ProtectedEnvelope);
	}

	[Fact]
	public async Task Store_RejectsAVersionThatIsNotTheOneOnOffer()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		IRecoveryEnvelopeService service = ctx.Envelopes(db);
		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, ctx.Clock.GetUtcNow().UtcDateTime);

		// Version 1 again (a stale/concurrent setup) and version 3 (a gap) are both refused: the version
		// is inside the client's own AAD, so the server can never renumber it on the client's behalf.
		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() =>
			service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, ctx.Clock.GetUtcNow().UtcDateTime));
		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() =>
			service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "3").Request, ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Theory]
	[InlineData("scrypt", 65536, 3, 1)]
	[InlineData("argon2id", 16, 3, 1)]
	[InlineData("argon2id", 65536, 99, 1)]
	[InlineData("argon2id", 65536, 3, 99)]
	public async Task Store_RejectsOutOfBoundsKdfMetadata(string algorithm, int memoryKiB, int iterations, int parallelism)
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		var (valid, _, _) = RecoveryTestContext.BuildEnvelope(account.Id, "1");
		StoreRecoveryEnvelopeRequest request = valid with
		{
			Kdf = new RecoveryKdf(algorithm, valid.Kdf.Salt, memoryKiB, iterations, parallelism),
		};

		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() =>
			ctx.Envelopes(db).StoreAsync(db, caller, request, ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Fact]
	public async Task Store_RejectsAVerificationKeyThatIsNotP256()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		var (valid, _, _) = RecoveryTestContext.BuildEnvelope(account.Id, "1");
		using ECDsa wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
		StoreRecoveryEnvelopeRequest request = valid with { VerificationKeySpki = Base64Url.Encode(wrongCurve.ExportSubjectPublicKeyInfo()) };

		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() =>
			ctx.Envelopes(db).StoreAsync(db, caller, request, ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Fact]
	public async Task Store_RefusesACallerWhoseDeviceWasRevokedAfterTheRequestWasAuthorized()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, device, session, caller) = await SeedTrustedCallerAsync(db, ctx);

		// The HTTP pipeline authorized this caller; the revocation commits before the mutation does.
		await using (AppDbContext revokeDb = fixture.CreateContext())
		{
			await ctx.Trust.RevokeDeviceAsync(revokeDb, account.Id, device.Id, device.TrustVersion, device.Id, ctx.Clock.GetUtcNow().UtcDateTime, session.Id);
		}

		await Assert.ThrowsAsync<SessionNotTrustedException>(() =>
			ctx.Envelopes(db).StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, ctx.Clock.GetUtcNow().UtcDateTime));

		await using AppDbContext verifyDb = fixture.CreateContext();
		Assert.False(await verifyDb.SyncRecoveryEnvelopes.AnyAsync(e => e.AccountId == account.Id));
	}

	[Fact]
	public async Task Store_RefusesACallerWhoseAccountContextEndedAfterTheRequestWasAuthorized()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);

		await using (AppDbContext freshDb = fixture.CreateContext())
		{
			await ctx.Accounts.StartNewAccountContextAsync(freshDb, account.LegacyUserId, "fresh-start-op-1", ctx.Clock.GetUtcNow().UtcDateTime);
		}

		await Assert.ThrowsAsync<SessionNotTrustedException>(() =>
			ctx.Envelopes(db).StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Fact]
	public async Task EveryUploadStaysAccepted_BecauseAnyOfThemMayBeTheSavedPassword()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		IRecoveryEnvelopeService service = ctx.Envelopes(db);
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;

		// Two setups whose confirmation never arrived (dismissed dialog, lost response), then one that completes.
		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, now);
		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "2").Request, now);
		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "3").Request, now);
		await service.ConfirmAsync(db, caller, "3", now);

		await using AppDbContext readDb = fixture.CreateContext();
		IReadOnlyList<RecoveryCandidate> candidates = await ctx.Envelopes(readDb).GetRecoveryCandidatesAsync(readDb, account.Id);

		// The server cannot know which attempt the system password manager actually kept, so none is retired.
		Assert.Equal([3L, 2L, 1L], candidates.Select(c => c.Version).ToArray());
		Assert.Equal(3, await readDb.SyncRecoveryEnvelopes.CountAsync(e => e.AccountId == account.Id));
	}

	[Fact]
	public async Task OnceAVersionIsConfirmed_NoFurtherSetupIsAccepted()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		IRecoveryEnvelopeService service = ctx.Envelopes(db);
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;

		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, now);
		await service.ConfirmAsync(db, caller, "1", now);

		// One password per account context: replacing it would race the password manager's single entry.
		await Assert.ThrowsAsync<RecoveryAlreadySetUpException>(() =>
			service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "2").Request, now));
		Assert.Equal(1, await db.SyncRecoveryEnvelopes.CountAsync(e => e.AccountId == account.Id));
	}

	[Fact]
	public async Task ConfirmingASecondVersion_IsRefusedAndLeavesTheFirstConfirmed()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		IRecoveryEnvelopeService service = ctx.Envelopes(db);
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;

		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, now);
		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "2").Request, now);
		await service.ConfirmAsync(db, caller, "2", now);

		await Assert.ThrowsAsync<RecoveryAlreadySetUpException>(() => service.ConfirmAsync(db, caller, "1", now));

		await using AppDbContext readDb = fixture.CreateContext();
		RecoveryEnvelopeView view = await ctx.Envelopes(readDb).GetCurrentAsync(readDb, account.Id);
		Assert.Equal("2", view.ConfirmedVersion);
		// The unconfirmed attempt is still a candidate: its password may be the one that was saved.
		Assert.Equal(["2", "1"], view.Candidates.Select(c => c.Version).ToArray());
	}

	[Fact]
	public async Task UnconfirmedUploads_AreCapped_WithoutDiscardingAnyOfThem()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		IRecoveryEnvelopeService service = ctx.Envelopes(db);
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;

		for (int version = 1; version <= 5; version++)
		{
			await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, version.ToString()).Request, now);
		}

		// The cap refuses the next attempt instead of dropping an older one that may be the saved password.
		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() =>
			service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "6").Request, now));

		await using AppDbContext readDb = fixture.CreateContext();
		IReadOnlyList<RecoveryCandidate> candidates = await ctx.Envelopes(readDb).GetRecoveryCandidatesAsync(readDb, account.Id);
		Assert.Equal([5L, 4L, 3L, 2L, 1L], candidates.Select(c => c.Version).ToArray());
	}

	[Fact]
	public async Task Confirm_RefusesAVersionThatDoesNotExist()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, _, caller) = await SeedTrustedCallerAsync(db, ctx);
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;
		await ctx.Envelopes(db).StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, now);

		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() => ctx.Envelopes(db).ConfirmAsync(db, caller, "7", now));
		// Confirming twice is harmless.
		await ctx.Envelopes(db).ConfirmAsync(db, caller, "1", now);
		await ctx.Envelopes(db).ConfirmAsync(db, caller, "1", now);
	}
}
