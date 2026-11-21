using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ServerDataTests;

/// <summary>
/// Proves a removed member's archive (status, metadata, member composition at exit) can be
/// reconstructed from the dedicated snapshot tables alone, even after the live/mutable tables it
/// was copied from have since changed and the per-chat change journal has been pruned - exactly
/// the scenario the snapshot tables exist for.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class FrozenArchiveReconstructionTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task RemovedMemberArchive_IsReconstructibleAfterLiveDataChangesAndJournalCleanup()
	{
		using AppDbContext db = fixture.CreateContext();

		SyncAccountEntity alice = await TestData.CreateAccountAsync(db); // will be removed
		SyncAccountEntity bob = await TestData.CreateAccountAsync(db);   // stays active
		SyncChatEntity chat = await TestData.CreateChatAsync(db, name: "Original chat name");

		SyncMembershipEntity aliceMembership = await TestData.CreateMembershipAsync(db, chat, alice);
		SyncMembershipEntity bobMembership = await TestData.CreateMembershipAsync(db, chat, bob);

		// Two messages from Bob while Alice is still a member; Alice has seen message0 but not message1 yet.
		// Real sequences start at 1 (0 is reserved for "nothing applied yet").
		SyncMessageEntity message0 = await TestData.CreateMessageAsync(db, chat, bobMembership, messageSequence: 1, changeSequence: 1);
		SyncMessageEntity message1 = await TestData.CreateMessageAsync(db, chat, bobMembership, messageSequence: 2, changeSequence: 2);

		DateTime seenAt = DateTime.UtcNow;
		db.SyncMessageRecipientStates.Add(new SyncMessageRecipientStateEntity(message0.Id, chat.Id, aliceMembership.Id, alice.Id, version: 1)
		{
			ReceivedAtUtc = seenAt,
			SeenAtUtc = seenAt,
			SeenReason = SyncSeenReason.Observed
		});
		db.SyncMessageRecipientStates.Add(new SyncMessageRecipientStateEntity(message1.Id, chat.Id, aliceMembership.Id, alice.Id, version: 1)
		{
			ReceivedAtUtc = seenAt
			// not seen yet at the moment Alice is removed
		});
		db.SyncChatChanges.Add(new SyncChatChangeEntity(chat.Id, 1, SyncChangeKind.Message, message0.Id, message0.CreatedAtUtc));
		db.SyncChatChanges.Add(new SyncChatChangeEntity(chat.Id, 2, SyncChangeKind.Message, message1.Id, message1.CreatedAtUtc));
		await db.SaveChangesAsync();

		// --- Alice is removed by Bob: freeze her membership and capture the snapshot bundle. ---
		DateTime removedAt = DateTime.UtcNow;
		const long removalChangeSequence = 3;

		aliceMembership.State = SyncMembershipState.Frozen;
		aliceMembership.EndedAtUtc = removedAt;
		aliceMembership.EndReason = SyncMembershipEndReason.Removed;
		aliceMembership.EndedByAccountId = bob.Id;

		// StateSnapshotId is not a stored column (Finding 4): for a frozen range, the snapshot
		// identity IS this grant's own MembershipId - the same id SyncMembershipMetadataSnapshotEntity
		// and SyncMembershipMessageStatusSnapshotEntity below are keyed/FK'd by.
		db.SyncMembershipHistoryGrants.Add(new SyncMembershipHistoryGrantEntity(aliceMembership.Id, fromMessageSequence: 1, throughMessageSequence: 2)
		{
			FrozenAtChangeSequence = removalChangeSequence
		});

		db.SyncMembershipMetadataSnapshots.Add(new SyncMembershipMetadataSnapshotEntity(aliceMembership.Id, chat.Name, chat.Kind, removalChangeSequence, removedAt));
		db.SyncMembershipMetadataSnapshotMembers.Add(new SyncMembershipMetadataSnapshotMemberEntity(
			aliceMembership.Id, chat.Id, bob.Id, bobMembership.Id, SyncMembershipState.Active));

		// Frozen per-recipient-per-message status, one row per recipient (Alice, the only recipient since Bob is the sender).
		db.SyncMembershipMessageStatusSnapshots.Add(new SyncMembershipMessageStatusSnapshotEntity(aliceMembership.Id, chat.Id, message0.Id, alice.Id)
		{
			ReceivedAtUtc = seenAt,
			SeenAtUtc = seenAt,
			SeenReason = SyncSeenReason.Observed
		});
		db.SyncMembershipMessageStatusSnapshots.Add(new SyncMembershipMessageStatusSnapshotEntity(aliceMembership.Id, chat.Id, message1.Id, alice.Id)
		{
			ReceivedAtUtc = seenAt
		});

		db.SyncChatChanges.Add(new SyncChatChangeEntity(chat.Id, removalChangeSequence, SyncChangeKind.MembershipEvent, aliceMembership.Id, removedAt)
		{
			MembershipEventKind = SyncMembershipEventKind.Removed
		});
		await db.SaveChangesAsync();

		// --- Time passes: live data for the still-active member changes, the chat is renamed, and the journal is pruned. ---
		SyncMessageRecipientStateEntity liveMessage1State = await db.SyncMessageRecipientStates
			.SingleAsync(r => r.MessageId == message1.Id && r.MembershipId == aliceMembership.Id);
		liveMessage1State.SeenAtUtc = DateTime.UtcNow; // Alice's archive must NOT reflect this later change.
		liveMessage1State.SeenReason = SyncSeenReason.Observed;

		chat.Name = "Renamed after Alice left";
		await db.SaveChangesAsync();

		await db.SyncChatChanges.Where(c => c.ChatId == chat.Id).ExecuteDeleteAsync(); // simulate journal retention cleanup

		// --- Reconstruct Alice's frozen archive using only the snapshot tables, independent of the mutated live state and the pruned journal. ---
		using AppDbContext readDb = fixture.CreateContext();

		SyncMembershipEntity frozenMembership = await readDb.SyncMemberships.SingleAsync(m => m.Id == aliceMembership.Id);
		Assert.Equal(SyncMembershipState.Frozen, frozenMembership.State);
		Assert.Equal(SyncMembershipEndReason.Removed, frozenMembership.EndReason);
		Assert.Equal(bob.Id, frozenMembership.EndedByAccountId);

		SyncMembershipMetadataSnapshotEntity metadata = await readDb.SyncMembershipMetadataSnapshots
			.Include(s => s.Members)
			.SingleAsync(s => s.MembershipId == aliceMembership.Id);
		Assert.Equal("Original chat name", metadata.ChatName); // not the later rename
		SyncMembershipMetadataSnapshotMemberEntity onlyOtherMember = Assert.Single(metadata.Members);
		Assert.Equal(bob.Id, onlyOtherMember.ParticipantAccountId);
		Assert.Equal(bobMembership.Id, onlyOtherMember.ParticipantMembershipIdAtSnapshot);

		Dictionary<string, SyncMembershipMessageStatusSnapshotEntity> frozenStatusesByMessage = await readDb.SyncMembershipMessageStatusSnapshots
			.Where(s => s.MembershipId == aliceMembership.Id)
			.ToDictionaryAsync(s => s.MessageId);

		Assert.NotNull(frozenStatusesByMessage[message0.Id].SeenAtUtc); // frozen as seen, as it was at removal
		Assert.Null(frozenStatusesByMessage[message1.Id].SeenAtUtc);    // frozen as NOT seen, unaffected by the later live Seen event

		Assert.Empty(await readDb.SyncChatChanges.Where(c => c.ChatId == chat.Id).ToListAsync()); // journal really is gone; reconstruction did not need it

		SyncMembershipHistoryGrantEntity grant = await readDb.SyncMembershipHistoryGrants.SingleAsync(g => g.MembershipId == aliceMembership.Id);
		Assert.Equal(1, grant.FromMessageSequence);
		Assert.Equal(2, grant.ThroughMessageSequence);
		Assert.Equal(removalChangeSequence, grant.FrozenAtChangeSequence);
	}

	[Fact]
	public async Task Rejoin_CreatesNewMembershipAndKeepsOldFrozenGrantAlongsideNewOverlappingOne()
	{
		using AppDbContext db = fixture.CreateContext();

		SyncAccountEntity alice = await TestData.CreateAccountAsync(db);
		SyncChatEntity chat = await TestData.CreateChatAsync(db);

		SyncMembershipEntity firstStint = await TestData.CreateMembershipAsync(db, chat, alice, rightsVersion: 1);
		firstStint.State = SyncMembershipState.Frozen;
		firstStint.EndedAtUtc = DateTime.UtcNow;
		firstStint.EndReason = SyncMembershipEndReason.Left;
		db.SyncMembershipHistoryGrants.Add(new SyncMembershipHistoryGrantEntity(firstStint.Id, fromMessageSequence: 1, throughMessageSequence: 99)
		{
			FrozenAtChangeSequence = 100
		});
		await db.SaveChangesAsync();

		// Rejoin: a brand new membership id, with a newly shared period that overlaps the old frozen archive (80-99) and extends past it (through 150).
		SyncMembershipEntity secondStint = await TestData.CreateMembershipAsync(db, chat, alice, rightsVersion: 2);
		Assert.NotEqual(firstStint.Id, secondStint.Id);
		db.SyncMembershipHistoryGrants.Add(new SyncMembershipHistoryGrantEntity(secondStint.Id, fromMessageSequence: 80, throughMessageSequence: null));
		await db.SaveChangesAsync();

		using AppDbContext readDb = fixture.CreateContext();
		List<SyncMembershipEntity> stints = await readDb.SyncMemberships
			.Where(m => m.ChatId == chat.Id && m.AccountId == alice.Id)
			.Include(m => m.HistoryGrants)
			.OrderBy(m => m.CreatedAtUtc)
			.ToListAsync();

		Assert.Equal(2, stints.Count);
		Assert.Equal(SyncMembershipState.Frozen, stints[0].State);
		Assert.Equal(1, stints[0].HistoryGrants.Single().FromMessageSequence);
		Assert.Equal(99, stints[0].HistoryGrants.Single().ThroughMessageSequence);

		Assert.Equal(SyncMembershipState.Active, stints[1].State);
		Assert.Equal(80, stints[1].HistoryGrants.Single().FromMessageSequence);
		Assert.Null(stints[1].HistoryGrants.Single().ThroughMessageSequence); // open-ended, "current"
		Assert.Null(stints[1].HistoryGrants.Single().FrozenAtChangeSequence); // not frozen - this is the active stint
	}
}
