namespace DuckNet.Contracts;

public sealed record FeeReleased(
    Guid AlarmId,
    string Reason)
{
    public const int Version = 1;

    public const string ReasonAlarmResolved = "AlarmResolved";
    public const string ReasonTimeout = "Timeout";
}
