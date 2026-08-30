using Testcontainers.RabbitMq;

namespace DuckNet.EventBus.Tests;

[Collection(RabbitMqCollection.Name)]
public class RabbitMqEventBusConformanceTests : EventBusConformanceTests, IAsyncLifetime
{
    private readonly RabbitMqFixture _fixture;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private RabbitMqEventBus? _bus;

    public RabbitMqEventBusConformanceTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IEventBus CreateBus() =>
        _bus ?? throw new InvalidOperationException("RabbitMQ bus is not started.");

    protected override string Group(string name) => $"{name}-{_id}";

    protected override Task WaitForSubscribersAsync() => Task.Delay(TimeSpan.FromSeconds(2));

    public Task InitializeAsync()
    {
        _bus = new RabbitMqEventBus(_fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_bus is not null)
        {
            await _bus.DisposeAsync();
        }
    }
}

[Collection(RabbitMqCollection.Name)]
public class RabbitMqReconnectTests
{
    private readonly RabbitMqFixture _fixture;

    public RabbitMqReconnectTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Consumer_recovers_after_broker_restart()
    {
        await using var bus = new RabbitMqEventBus(_fixture.ConnectionString);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var group = $"reconnect-{Guid.NewGuid():N}";
        var envelope = TestEnvelope.Squeak();
        var received = EventBusConformanceTests.TakeAsync(
            bus.SubscribeAsync(group, cts.Token),
            1,
            cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);

        var stop = await _fixture.Container.ExecAsync(["rabbitmqctl", "stop_app"]);
        Assert.Equal(0, stop.ExitCode);

        var publish = bus.PublishAsync(envelope, cts.Token).AsTask();

        var start = await _fixture.Container.ExecAsync(["rabbitmqctl", "start_app"]);
        Assert.Equal(0, start.ExitCode);

        await publish;
        var actual = Assert.Single(await received);
        Assert.Equal(envelope.EventId, actual.EventId);
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMq";
}

public sealed class RabbitMqFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } = new RabbitMqBuilder().Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public async Task DisposeAsync() => await Container.DisposeAsync();
}
