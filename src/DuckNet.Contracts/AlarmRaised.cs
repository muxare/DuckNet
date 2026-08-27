namespace DuckNet.Contracts;

public sealed record AlarmRaised(
    string DuckId,
    double Rate,
    DateTimeOffset WindowStart);
