using System.Globalization;
using Services.Sessions;
using Services.Sync.Contracts;

namespace Services.Sync.Service;

/// <summary>
/// The two encodings every sync surface shares: canonical decimal sequences and the fixed-shape stream
/// positions built from them. One place, so a page, a boundary and a position exchange can never
/// describe the same stream differently.
/// </summary>
internal static class SyncWire
{
	public static string Seq(long value) => value.ToString(CultureInfo.InvariantCulture);

	public static bool TryParseSeq(string? value, out long parsed)
	{
		parsed = 0;
		return SyncProtocol.IsSequence(value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
	}

	/// <summary>docs/sync-contract-v1.md: "Catalog StreamPosition requires null ChatId/MembershipId; Chat requires both."</summary>
	public static StreamPosition CatalogPosition(long sequence) =>
		new(StreamKind.Catalog, null, null, Seq(sequence));

	public static StreamPosition ChatPosition(string chatId, string membershipId, long sequence) =>
		new(StreamKind.Chat, chatId, membershipId, Seq(sequence));

	/// <summary>
	/// Whether a client-supplied position is even shaped like a position of its own stream kind. The two
	/// ids are held to the contract's own id-token rule, which also keeps them free of the separators a
	/// batch digest is built from - so two different batches of one session cannot hash alike and let a
	/// CHANGED batch be answered with the saved result of the original.
	/// </summary>
	public static bool IsWellFormed(StreamPosition? position) =>
		position is not null
		&& SyncProtocol.IsSequence(position.Sequence)
		&& (position.StreamKind == StreamKind.Catalog
			? position.ChatId is null && position.MembershipId is null
			: RestrictedAsciiId.IsValid(position.ChatId) && RestrictedAsciiId.IsValid(position.MembershipId));
}
