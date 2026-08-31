namespace DuckNet.Kernel.Persistence;

/// <summary>
/// Picks SQLite (local) vs Postgres (Azure) from the environment. Aspire never
/// sets <c>DUCKNET_POSTGRES_CONNECTION</c>, so the laptop demo stays SQLite.
/// </summary>
public static class KernelDbFactory
{
    public static bool UsePostgres => !string.IsNullOrWhiteSpace(PostgresConnectionString());

    public static string? PostgresConnectionString() =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("DUCKNET_POSTGRES_CONNECTION"),
            Environment.GetEnvironmentVariable("ConnectionStrings__postgres"));

    public static KernelDb OpenSqlite(string path, string schema) => KernelDb.Open(path, schema);

    public static PostgresKernelDb OpenPostgres(string schema) =>
        PostgresKernelDb.Open(
            PostgresConnectionString()
            ?? throw new InvalidOperationException("DUCKNET_POSTGRES_CONNECTION is not set."),
            schema);

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
