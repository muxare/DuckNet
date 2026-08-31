using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DuckNet.Kernel.Tests;

[Collection(PostgresCollection.Name)]
public class PostgresProviderTests
{
    private readonly PostgresFixture _fixture;

    public PostgresProviderTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Kernel_db_factory_stays_sqlite_without_env()
    {
        if (KernelDbFactory.PostgresConnectionString() is not null)
        {
            return;
        }

        Assert.False(KernelDbFactory.UsePostgres);
    }

    [Fact]
    public void Four_center_databases_own_their_tables()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        using var telemetry = Open("telemetry", PostgresSchema.Telemetry);
        using var alarm = Open("alarm", PostgresSchema.Alarm);
        using var dashboard = Open("dashboard", PostgresSchema.Dashboard);
        using var billing = Open("billing", PostgresSchema.Billing);

        Assert.Contains("event_log", telemetry.TableNames());
        Assert.DoesNotContain("event_log", alarm.TableNames());
        Assert.DoesNotContain("event_log", dashboard.TableNames());
        Assert.DoesNotContain("event_log", billing.TableNames());
        Assert.Contains("squeak_window", alarm.TableNames());
        Assert.Contains("squeaks_by_duck_hour", dashboard.TableNames());
        Assert.Contains("billing_sagas", billing.TableNames());
        Assert.Contains("dead_letter_queue", alarm.TableNames());
        Assert.Contains("dead_letter_queue", dashboard.TableNames());
        Assert.Contains("dead_letter_queue", billing.TableNames());
    }

    [Fact]
    public void Event_log_append_is_idempotent_on_event_id_and_round_trips_wire_fields()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        using var db = Open("telemetry", PostgresSchema.Telemetry);
        var envelope = TestSqueak();

        var first = db.Write((conn, tx) => PostgresPersistence.AppendEventLog(conn, tx, envelope));
        var second = db.Write((conn, tx) => PostgresPersistence.AppendEventLog(conn, tx, envelope));
        Assert.Equal(first, second);

        var rows = db.Read(conn => PostgresPersistence.ReadEventLogAfter(conn, 0, 10));
        var actual = Assert.Single(rows);
        Assert.Equal(envelope.EventId, actual.EventId);
        Assert.Equal(envelope.Type, actual.Type);
        Assert.Equal(envelope.Version, actual.Version);
        Assert.Equal(envelope.PartitionKey, actual.PartitionKey);
        Assert.Equal(envelope.PayloadJson, actual.PayloadJson);
        Assert.Equal(envelope.TraceId, actual.TraceId);
        Assert.Equal(envelope.CausationId, actual.CausationId);
        Assert.Equal(first, actual.LogOffset);
    }

    [Fact]
    public void Inbox_conflict_does_not_insert_a_second_row()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        using var db = Open("alarm", PostgresSchema.Alarm);
        var eventId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var first = db.Write((conn, tx) =>
            PostgresPersistence.TryInsertInbox(conn, tx, "alarm-center", eventId));
        var second = db.Write((conn, tx) =>
            PostgresPersistence.TryInsertInbox(conn, tx, "alarm-center", eventId));

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void Outbox_mark_published_clears_unpublished()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        using var db = Open("alarm", PostgresSchema.Alarm);
        var envelope = TestSqueak();

        var id = db.Write((conn, tx) => PostgresPersistence.InsertOutbox(conn, tx, envelope));
        Assert.Equal(1, db.Read(PostgresPersistence.UnpublishedOutboxCount));

        db.Write((conn, tx) =>
            PostgresPersistence.MarkOutboxPublished(conn, tx, id, DateTimeOffset.UtcNow));
        Assert.Equal(0, db.Read(PostgresPersistence.UnpublishedOutboxCount));
    }

    private PostgresKernelDb Open(string database, string schema)
    {
        _fixture.EnsureDatabase(database);
        return PostgresKernelDb.Open(_fixture.ConnectionString(database), schema);
    }

    private static EventEnvelope TestSqueak() =>
        SqueakedEnvelope.Create(
            new Squeaked("duck-1", 1, DateTimeOffset.Parse("2026-08-31T12:00:00Z")),
            eventId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            traceId: "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
            causationId: "parent-1");
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly object _gate = new();
    private readonly HashSet<string> _created = new(StringComparer.Ordinal);
    private PostgreSqlContainer? _container;

    /// <summary>False when Docker is unavailable; container tests no-op so plain `dotnet test` stays green.</summary>
    public bool IsAvailable => _container is not null;

    private PostgreSqlContainer Container =>
        _container ?? throw new InvalidOperationException("Postgres container unavailable — guard with IsAvailable.");

    public async Task InitializeAsync()
    {
        // Build() already probes the Docker endpoint, so it stays inside the guard.
        PostgreSqlContainer? container = null;
        try
        {
            container = new PostgreSqlBuilder().Build();
            await container.StartAsync();
            _container = container;
        }
        catch
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public string ConnectionString(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = database
        };
        return builder.ConnectionString;
    }

    public void EnsureDatabase(string database)
    {
        lock (_gate)
        {
            if (!_created.Add(database))
            {
                return;
            }
        }

        using var admin = new NpgsqlConnection(Container.GetConnectionString());
        admin.Open();
        using var exists = admin.CreateCommand();
        exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @n";
        exists.Parameters.AddWithValue("n", database);
        if (exists.ExecuteScalar() is not null)
        {
            return;
        }

        using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE {database}";
        create.ExecuteNonQuery();
    }
}
