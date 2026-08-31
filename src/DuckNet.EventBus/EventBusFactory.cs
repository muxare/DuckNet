using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Environment-selected <see cref="IEventBus"/>. Local Aspire injects RabbitMQ;
/// Azure Container Apps set Service Bus (connection string or namespace + MI).
/// Tests and the kernel leave both unset and get <see cref="InMemoryEventBus"/>.
/// Center handlers stay on <see cref="IEventBus"/> either way.
/// </summary>
public static class EventBusFactory
{
    public const string DefaultExchange = "ducknet.events";
    public const string DefaultTopic = ServiceBusEventBus.DefaultTopic;

    public static IEventBus Create() => Create(FromEnvironment());

    public static IEventBus Create(EventBusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ServiceBusConnection))
        {
            return new ServiceBusEventBus(options.ServiceBusConnection, options.Topic);
        }

        if (!string.IsNullOrWhiteSpace(options.ServiceBusNamespace))
        {
            return new ServiceBusEventBus(
                options.ServiceBusNamespace,
                new Azure.Identity.DefaultAzureCredential(),
                options.Topic);
        }

        if (!string.IsNullOrWhiteSpace(options.RabbitMqConnection))
        {
            return new RabbitMqEventBus(options.RabbitMqConnection, options.Exchange);
        }

        return new InMemoryEventBus();
    }

    public static EventBusOptions FromEnvironment() => new(
        RabbitMqConnection: ConnectionString(),
        ServiceBusConnection: ServiceBusConnectionString(),
        ServiceBusNamespace: FirstNonEmpty(
            Environment.GetEnvironmentVariable("DUCKNET_SERVICEBUS_NAMESPACE")),
        Exchange: ExchangeName(),
        Topic: TopicName());

    public static string? ConnectionString() =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("ConnectionStrings__rabbitmq"),
            Environment.GetEnvironmentVariable("RABBITMQ_CONNECTION"));

    public static string? ServiceBusConnectionString() =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("ConnectionStrings__servicebus"),
            Environment.GetEnvironmentVariable("DUCKNET_SERVICEBUS_CONNECTION"));

    public static string ExchangeName() =>
        FirstNonEmpty(Environment.GetEnvironmentVariable("DUCKNET_BUS_EXCHANGE"))
        ?? DefaultExchange;

    public static string TopicName() =>
        FirstNonEmpty(Environment.GetEnvironmentVariable("DUCKNET_BUS_TOPIC"))
        ?? DefaultTopic;

    public static bool IsBrokerBacked(IEventBus bus) =>
        bus is RabbitMqEventBus or ServiceBusEventBus;

    internal static string? FirstNonEmpty(params string?[] values)
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

public sealed record EventBusOptions(
    string? RabbitMqConnection = null,
    string? ServiceBusConnection = null,
    string? ServiceBusNamespace = null,
    string? Exchange = null,
    string? Topic = null);
