using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// One hop in a version chain (vN → vN+1). Contracts stay immutable;
/// upcasters live here so Centers never fork payload types in handlers.
/// </summary>
public interface IEventUpcaster
{
    bool CanUpcast(string type, int version);

    EventEnvelope Upcast(EventEnvelope source);
}
