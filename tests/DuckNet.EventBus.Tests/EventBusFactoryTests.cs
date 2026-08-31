using DuckNet.EventBus;

namespace DuckNet.EventBus.Tests;

public class EventBusFactoryTests
{
    [Fact]
    public void Create_without_connection_string_returns_in_memory()
    {
        if (EventBusFactory.ConnectionString() is not null
            || EventBusFactory.ServiceBusConnectionString() is not null)
        {
            return;
        }

        var bus = EventBusFactory.Create();
        Assert.IsType<InMemoryEventBus>(bus);
        Assert.False(EventBusFactory.IsBrokerBacked(bus));
    }

    [Fact]
    public void Create_with_service_bus_connection_selects_service_bus()
    {
        var bus = EventBusFactory.Create(new EventBusOptions(
            ServiceBusConnection:
            "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=dGVzdA==",
            RabbitMqConnection: "amqp://guest:guest@localhost:5672/"));
        Assert.IsType<ServiceBusEventBus>(bus);
        Assert.True(EventBusFactory.IsBrokerBacked(bus));
    }

    [Fact]
    public void Create_with_rabbit_only_selects_rabbit()
    {
        var bus = EventBusFactory.Create(new EventBusOptions(
            RabbitMqConnection: "amqp://guest:guest@localhost:5672/"));
        Assert.IsType<RabbitMqEventBus>(bus);
        Assert.True(EventBusFactory.IsBrokerBacked(bus));
    }

    [Fact]
    public void Event_hubs_writer_is_null_without_env()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DUCKNET_EVENTHUBS_CONNECTION"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DUCKNET_EVENTHUBS_NAMESPACE"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__eventhubs")))
        {
            return;
        }

        Assert.Null(EventHubsLogWriterFactory.TryCreate());
    }

    [Fact]
    public void Event_hubs_partition_key_is_envelope_partition_key()
    {
        var envelope = TestEnvelope.Squeak(duckId: "loud-duck");
        Assert.Equal("loud-duck", EventHubsLogWriter.PartitionKeyFor(envelope));
    }
}

public class ServiceBusEventBusLiveTests
{
    [Fact]
    public async Task Two_groups_each_receive_a_copy_when_configured()
    {
        var connection = Environment.GetEnvironmentVariable("DUCKNET_SERVICEBUS_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__servicebus");
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }

        await using var bus = new ServiceBusEventBus(connection);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = Guid.NewGuid().ToString("N");
        var envelope = TestEnvelope.Squeak();

        var groupA = EventBusConformanceTests.TakeAsync(
            bus.SubscribeAsync($"group-a-{id}", cts.Token), 1, cts.Token);
        var groupB = EventBusConformanceTests.TakeAsync(
            bus.SubscribeAsync($"group-b-{id}", cts.Token), 1, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        await bus.PublishAsync(envelope, cts.Token);

        Assert.Equal(envelope.EventId, Assert.Single(await groupA).EventId);
        Assert.Equal(envelope.EventId, Assert.Single(await groupB).EventId);
    }
}

public class EventHubsLogWriterTests
{
    [Fact]
    public async Task Append_when_configured()
    {
        var connection = Environment.GetEnvironmentVariable("DUCKNET_EVENTHUBS_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__eventhubs");
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }

        await using var writer = new EventHubsLogWriter(connection);
        await writer.AppendAsync(TestEnvelope.Squeak());
    }
}
