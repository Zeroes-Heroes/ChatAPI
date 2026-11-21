namespace Database.Enums.Sync;

public enum SyncOutboxStatus
{
	Pending = 0,
	Leased = 1,
	Published = 2,
	Failed = 3
}
