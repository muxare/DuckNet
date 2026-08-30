namespace DuckNet.EventBus.Tests;

public class InMemoryEventBusConformanceTests : EventBusConformanceTests
{
    protected override IEventBus CreateBus() => new InMemoryEventBus();
}
