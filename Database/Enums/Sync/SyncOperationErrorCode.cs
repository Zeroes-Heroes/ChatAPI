namespace Database.Enums.Sync;

public enum SyncOperationErrorCode
{
	Unavailable = 0,
	RateLimited = 1,
	SessionExpired = 2,
	DeviceRevoked = 3,
	MembershipChanged = 4,
	Forbidden = 5,
	InvalidPackage = 6,
	OperationConflict = 7,
	UnsupportedVersion = 8,
	PayloadTooLarge = 9
}
