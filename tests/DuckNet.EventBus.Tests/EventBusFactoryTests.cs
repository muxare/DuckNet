namespace DuckNet.EventBus.Tests;

public class EventBusFactoryTests
{
    [Fact]
    public void Create_without_connection_string_returns_in_memory()
    {
        if (EventBusFactory.ConnectionString() is not null)
        {
            return;
        }

        var bus = EventBusFactory.Create();
        Assert.IsType<InMemoryEventBus>(bus);
        Assert.False(EventBusFactory.IsBrokerBacked(bus));
    }
}
