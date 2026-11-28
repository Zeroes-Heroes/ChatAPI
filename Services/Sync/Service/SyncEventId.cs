using System.Globalization;
using Services.Delivery.Models;

namespace Services.Sync.Service;

/// <summary>
/// The one place a sync change's EventId is formed. Ids are derived from WHAT a change says - the
/// entity it describes and that entity's version - and never from the page or the position that
/// happened to carry it, because the same fact legitimately reaches a device by several routes: a full
/// restoration, a delta page and a live event. "EventId deduplicates delivery" only works if all three
/// agree, so a message deliberately reuses <see cref="DeliveryEventId.ForChatChange"/>, the id stage
/// 12's live delivery already puts on that same chat change (a message is immutable, so its change
/// position IS its version).
/// </summary>
internal static class SyncEventId
{
	public static string Message(string chatId, long changeSequence) =>
		DeliveryEventId.ForChatChange(chatId, changeSequence);

	public static string RecipientState(string chatId, string messageId, string accountId, string version) =>
		$"{chatId}:recipientState:{messageId}:{accountId}:{version}";

	public static string DeviceState(string chatId, string messageId, string localInstanceId, string version) =>
		$"{chatId}:deviceState:{messageId}:{localInstanceId}:{version}";

	public static string Unread(string chatId, string membershipId, string version) =>
		$"{chatId}:unread:{membershipId}:{version}";

	public static string Membership(string chatId, string membershipId, string rightsVersion) =>
		$"{chatId}:membership:{membershipId}:{rightsVersion}";

	/// <summary>A stint has at most one Added and one end event, so the stint and the kind name it for good - no position needed.</summary>
	public static string MembershipEvent(string chatId, string membershipId, string kind) =>
		$"{chatId}:membershipEvent:{membershipId}:{kind}";

	public static string Chat(string chatId, long version) =>
		$"catalog:{chatId}:{version.ToString(CultureInfo.InvariantCulture)}";
}
