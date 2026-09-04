namespace DuckNet.Contracts;

public sealed record PartsReleased(
    Guid AlarmId,
    string Reason)
{
    public const int Version = 1;

    public const string ReasonAlarmResolved = "AlarmResolved";
    public const string ReasonTimeout = "Timeout";
    public const string ReasonDeclined = "Declined";
}
