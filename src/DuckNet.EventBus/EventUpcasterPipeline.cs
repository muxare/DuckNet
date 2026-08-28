using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Applies matching upcasters until the envelope is at the current version.
/// Unknown types and already-current versions pass through unchanged.
/// </summary>
public sealed class EventUpcasterPipeline
{
    public static EventUpcasterPipeline Default { get; } = new(new SqueakedV1ToV2Upcaster());

    private readonly IReadOnlyList<IEventUpcaster> _upcasters;

    public EventUpcasterPipeline(params IEventUpcaster[] upcasters)
    {
        _upcasters = upcasters;
    }

    public EventEnvelope Upcast(EventEnvelope source)
    {
        var current = source;
        var hops = 0;
        while (true)
        {
            var upcaster = Find(current.Type, current.Version);
            if (upcaster is null)
            {
                return current;
            }

            var next = upcaster.Upcast(current);
            if (next.Version <= current.Version)
            {
                throw new InvalidOperationException(
                    $"{upcaster.GetType().Name} did not increase Version ({current.Version} → {next.Version}).");
            }

            current = next;
            if (++hops > 32)
            {
                throw new InvalidOperationException("Upcaster chain exceeded 32 hops.");
            }
        }
    }

    private IEventUpcaster? Find(string type, int version)
    {
        foreach (var upcaster in _upcasters)
        {
            if (upcaster.CanUpcast(type, version))
            {
                return upcaster;
            }
        }

        return null;
    }
}
