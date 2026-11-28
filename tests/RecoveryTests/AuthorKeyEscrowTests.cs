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

namespace RecoveryTests;

/// <summary>
/// The escrow is the update path the frozen envelope cannot provide. The server stores sealed entries
/// it cannot open, keyed to a version it already has; everything here is about what it refuses and
/// what it never changes.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuthorKeyEscrowTests(DatabaseFixture fixture)
{
	private static AuthorKeyEscrowEntry Entry(string version, string authorKeyId, string chatId = "chat-1", byte[]? ciphertext = null)
	{
		using ECDiffieHellman ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
		return new AuthorKeyEscrowEntry(
			version,
			authorKeyId,
			chatId,
			"1",
			Base64Url.Encode(ephemeral.ExportSubjectPublicKeyInfo()),
			new EncryptedContent(
				Base64Url.Encode(RandomNumberGenerator.GetBytes(12)),
				Base64Url.Encode(ciphertext ?? RandomNumberGenerator.GetBytes(48)),
				Base64Url.Encode(RandomNumberGenerator.GetBytes(16))));
	}

	private async Task<(SyncAccountEntity Account, ClaimsPrincipal Caller, IRecoveryEnvelopeService Service,
		SyncDeviceRegistrationEntity Device, SyncDeviceSessionEntity Session)>
		SeedConfirmedRecoveryAsync(AppDbContext db, RecoveryTestContext ctx)
	{
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		SyncLocalInstanceEntity instance = await TestData.CreateLocalInstanceAsync(db, device);
		SyncDeviceSessionEntity session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");
		ClaimsPrincipal caller = ctx.AppSessionPrincipal(
			account.LegacyUserId, account.Id, session.Id, device.Id, instance.Id, device.TrustVersion);

		IRecoveryEnvelopeService service = ctx.Envelopes(db);
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;
		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, now);
		await service.ConfirmAsync(db, caller, "1", now);
		return (account, caller, service, device, session);
	}

	[Fact]
	public async Task StoredEntries_RoundTripByteForByte_AndAreNotReadableFromTheDatabaseAlone()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, caller, service, _, _) = await SeedConfirmedRecoveryAsync(db, ctx);
		AuthorKeyEscrowEntry entry = Entry("1", "authkey-1");

		await service.StoreAuthorKeyEscrowsAsync(
			db, caller, new StoreAuthorKeyEscrowRequest(SyncProtocol.Version, [entry]), ctx.Clock.GetUtcNow().UtcDateTime);

		await using AppDbContext readDb = fixture.CreateContext();
		AuthorKeyEscrowEntry stored = Assert.Single(await ctx.Envelopes(readDb).GetAuthorKeyEscrowsAsync(readDb, account.Id));
		Assert.Equal(entry, stored);

		SyncAuthorKeyEscrowEntity row = await readDb.SyncAuthorKeyEscrows.AsNoTracking().SingleAsync(e => e.AccountId == account.Id);
		Assert.NotEqual(RecoveryTestContext.DecodeBase64Url(entry.Content.Ciphertext)!, row.ProtectedPayload);
	}

	/// <summary>
	/// Every ACCEPTED version must be sealable, not only the confirmed one: an upload whose confirmation
	/// was lost may be exactly the password the system manager kept, and the recovery routes accept it.
	/// </summary>
	[Fact]
	public async Task EveryCandidate_CarriesItsOwnVerificationKey_ForSealing()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		SyncAccountEntity account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		SyncDeviceRegistrationEntity device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		SyncLocalInstanceEntity instance = await TestData.CreateLocalInstanceAsync(db, device);
		SyncDeviceSessionEntity session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");
		ClaimsPrincipal caller = ctx.AppSessionPrincipal(
			account.LegacyUserId, account.Id, session.Id, device.Id, instance.Id, device.TrustVersion);
		IRecoveryEnvelopeService service = ctx.Envelopes(db);
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;

		// v1's confirmation is lost; another device's v2 is the one that gets confirmed.
		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "1").Request, now);
		await service.StoreAsync(db, caller, RecoveryTestContext.BuildEnvelope(account.Id, "2").Request, now);
		await service.ConfirmAsync(db, caller, "2", now);

		RecoveryEnvelopeView view = await service.GetCurrentAsync(db, account.Id);

		Assert.Equal("2", view.ConfirmedVersion);
		foreach (RecoveryPackage candidate in view.Candidates)
		{
			SyncRecoveryEnvelopeEntity row = await db.SyncRecoveryEnvelopes.AsNoTracking()
				.SingleAsync(e => e.AccountId == account.Id && e.RecoveryVersion == long.Parse(candidate.Version));
			Assert.Equal(Base64Url.Encode(row.VerificationSpki), candidate.VerificationKeySpki);
		}
		// Both of them, so a device can escrow to v1 as well - the secret the user may actually hold.
		Assert.Equal(["2", "1"], view.Candidates.Select(c => c.Version).ToArray());
	}

	/// <summary>The same key is reachable on the bootstrap route, where a recovering device needs no session.</summary>
	[Fact]
	public async Task EveryCandidate_CarriesItsOwnVerificationKey_OnTheBootstrapRoute()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, _, service, _, _) = await SeedConfirmedRecoveryAsync(db, ctx);

		IReadOnlyList<Services.Recovery.Models.RecoveryCandidate> candidates =
			await service.GetRecoveryCandidatesAsync(db, account.Id);

		Services.Recovery.Models.RecoveryCandidate candidate = Assert.Single(candidates);
		Assert.Equal(Base64Url.Encode(candidate.VerificationSpki), candidate.Package.VerificationKeySpki);
	}

	[Fact]
	public async Task ReUploadingTheSameAuthorKey_ChangesNothing()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, caller, service, _, _) = await SeedConfirmedRecoveryAsync(db, ctx);
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;
		AuthorKeyEscrowEntry first = Entry("1", "authkey-1");

		Assert.Equal(1, await service.StoreAuthorKeyEscrowsAsync(db, caller, new StoreAuthorKeyEscrowRequest(SyncProtocol.Version, [first]), now));
		// A retry after a lost response must not rewrite an entry a recovering device may already read.
		Assert.Equal(0, await service.StoreAuthorKeyEscrowsAsync(
			db, caller, new StoreAuthorKeyEscrowRequest(SyncProtocol.Version, [Entry("1", "authkey-1")]), now));

		await using AppDbContext readDb = fixture.CreateContext();
		Assert.Equal(first, Assert.Single(await ctx.Envelopes(readDb).GetAuthorKeyEscrowsAsync(readDb, account.Id)));
	}

	[Fact]
	public async Task AVersionThisAccountContextDoesNotHave_IsRefused()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, caller, service, _, _) = await SeedConfirmedRecoveryAsync(db, ctx);

		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() => service.StoreAuthorKeyEscrowsAsync(
			db, caller, new StoreAuthorKeyEscrowRequest(SyncProtocol.Version, [Entry("7", "authkey-1")]), ctx.Clock.GetUtcNow().UtcDateTime));

		await using AppDbContext readDb = fixture.CreateContext();
		Assert.Empty(await ctx.Envelopes(readDb).GetAuthorKeyEscrowsAsync(readDb, account.Id));
	}

	[Theory]
	[InlineData("01", "authkey-1", "chat-1")]      // non-canonical version
	[InlineData("1", "author key", "chat-1")]      // not a restricted ASCII id
	[InlineData("1", "authkey-1", "chat/1")]
	public async Task MalformedEntries_AreRefusedBeforeAnythingIsWritten(string version, string authorKeyId, string chatId)
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, caller, service, _, _) = await SeedConfirmedRecoveryAsync(db, ctx);

		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() => service.StoreAuthorKeyEscrowsAsync(
			db, caller, new StoreAuthorKeyEscrowRequest(SyncProtocol.Version, [Entry(version, authorKeyId, chatId)]),
			ctx.Clock.GetUtcNow().UtcDateTime));

		await using AppDbContext readDb = fixture.CreateContext();
		Assert.Empty(await ctx.Envelopes(readDb).GetAuthorKeyEscrowsAsync(readDb, account.Id));
	}

	[Fact]
	public async Task AnEntryWithBadFraming_IsRefused()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (_, caller, service, _, _) = await SeedConfirmedRecoveryAsync(db, ctx);
		AuthorKeyEscrowEntry entry = Entry("1", "authkey-1");
		DateTime now = ctx.Clock.GetUtcNow().UtcDateTime;

		foreach (EncryptedContent content in new[]
		{
			entry.Content with { Nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(11)) },
			entry.Content with { Tag = Base64Url.Encode(RandomNumberGenerator.GetBytes(15)) },
			entry.Content with { Ciphertext = Base64Url.Encode([]) },
			entry.Content with { Nonce = "not base64url!!" },
		})
		{
			await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() => service.StoreAuthorKeyEscrowsAsync(
				db, caller, new StoreAuthorKeyEscrowRequest(SyncProtocol.Version, [entry with { Content = content }]), now));
		}
	}

	[Fact]
	public async Task AnEphemeralKeyThatIsNotP256_IsRefused()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (_, caller, service, _, _) = await SeedConfirmedRecoveryAsync(db, ctx);
		using ECDiffieHellman p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

		await Assert.ThrowsAsync<RecoveryEnvelopeInvalidException>(() => service.StoreAuthorKeyEscrowsAsync(
			db, caller,
			new StoreAuthorKeyEscrowRequest(SyncProtocol.Version, [
				Entry("1", "authkey-1") with { EphemeralPublicKeySpki = Base64Url.Encode(p384.ExportSubjectPublicKeyInfo()) },
			]),
			ctx.Clock.GetUtcNow().UtcDateTime));
	}

	[Fact]
	public async Task ACallerRevokedAfterTheRequestWasAuthorized_CannotAddEntries()
	{
		using RecoveryTestContext ctx = new();
		await using AppDbContext db = fixture.CreateContext();
		var (account, caller, _, device, session) = await SeedConfirmedRecoveryAsync(db, ctx);

		// The HTTP pipeline authorized this caller; the revocation commits before the mutation does.
		await using (AppDbContext revokeDb = fixture.CreateContext())
		{
			await ctx.Trust.RevokeDeviceAsync(
				revokeDb, account.Id, device.Id, device.TrustVersion, device.Id, ctx.Clock.GetUtcNow().UtcDateTime, session.Id);
		}

		await Assert.ThrowsAsync<SessionNotTrustedException>(() => ctx.Envelopes(db).StoreAuthorKeyEscrowsAsync(
			db, caller, new StoreAuthorKeyEscrowRequest(SyncProtocol.Version, [Entry("1", "authkey-1")]),
			ctx.Clock.GetUtcNow().UtcDateTime));

		await using AppDbContext readDb = fixture.CreateContext();
		Assert.Empty(await ctx.Envelopes(readDb).GetAuthorKeyEscrowsAsync(readDb, account.Id));
	}
}
