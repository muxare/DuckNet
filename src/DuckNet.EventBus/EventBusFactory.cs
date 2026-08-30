namespace DuckNet.EventBus;

/// <summary>
/// Picks the <see cref="IEventBus"/> implementation from the environment.
/// Aspire injects <c>ConnectionStrings__rabbitmq</c>; tests and the kernel
/// leave it unset and get <see cref="InMemoryEventBus"/>. Center handlers
/// stay on <see cref="IEventBus"/> either way.
/// </summary>
public static class EventBusFactory
{
    public const string DefaultExchange = "ducknet.events";

    public static IEventBus Create()
    {
        var connection = ConnectionString();
        if (string.IsNullOrWhiteSpace(connection))
        {
            return new InMemoryEventBus();
        }

        return new RabbitMqEventBus(connection, ExchangeName());
    }

    public static string? ConnectionString() =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("ConnectionStrings__rabbitmq"),
            Environment.GetEnvironmentVariable("RABBITMQ_CONNECTION"));

    public static string ExchangeName() =>
        FirstNonEmpty(Environment.GetEnvironmentVariable("DUCKNET_BUS_EXCHANGE"))
        ?? DefaultExchange;

    public static bool IsBrokerBacked(IEventBus bus) => bus is RabbitMqEventBus;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
