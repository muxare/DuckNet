namespace DuckNet.Kernel.Producer;

/// <summary>
/// Lab stand-in for a mine-site uplink. When down, the edge buffer holds
/// readings with their original device <c>OccurredAt</c>.
/// </summary>
public sealed class UplinkGate
{
    private readonly object _lock = new();
    private bool _up = true;
    private DateTimeOffset? _downUntil;

    public bool IsUp
    {
        get
        {
            lock (_lock)
            {
                if (_downUntil is { } until && DateTimeOffset.UtcNow >= until)
                {
                    _up = true;
                    _downUntil = null;
                }

                return _up;
            }
        }
    }

    public void Restore()
    {
        lock (_lock)
        {
            _up = true;
            _downUntil = null;
        }
    }

    public void TakeDown(TimeSpan? duration = null)
    {
        lock (_lock)
        {
            _up = false;
            _downUntil = duration is { } d
                ? DateTimeOffset.UtcNow + d
                : null;
        }
    }
}
