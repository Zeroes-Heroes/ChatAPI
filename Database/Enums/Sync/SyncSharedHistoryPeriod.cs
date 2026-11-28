namespace Database.Enums.Sync;

/// <summary>
/// The period an adder chose to share when adding a participant (plan/08-history-archives.md,
/// MESSAGE_SYNC_ANALYSIS.md: "Добавящият избира периода за споделяне, включително без предишна
/// история"). Stored on every granted window so the chosen option - not only its computed boundary -
/// stays recoverable, and so a window can be told apart from one produced by a different option that
/// happened to resolve to the same instant.
///
/// The boundary itself is computed ONCE, at the moment of the add, from the server clock (see
/// Services.Membership.Models.SharedHistorySelection) and persisted: "Изчислената граница се запазва
/// за всички устройства."
/// </summary>
public enum SyncSharedHistoryPeriod
{
	/// <summary>No previous history. The stint may read only what is created from its own add onwards.</summary>
	None = 0,

	/// <summary>The last 24 hours before the add.</summary>
	Last24Hours = 1,

	/// <summary>Ten days back from the add.</summary>
	LastTenDays = 2,

	/// <summary>The adder's current calendar day, up to the add, in the adder's own time zone.</summary>
	CurrentCalendarDay = 3
}
