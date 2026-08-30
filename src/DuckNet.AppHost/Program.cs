var builder = DistributedApplication.CreateBuilder(args);

var dataDir = Path.Combine(builder.AppHostDirectory, "data");
Directory.CreateDirectory(dataDir);

var telemetry = builder.AddProject<Projects.DuckNet_TelemetryCenter>("telemetry")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("DUCKNET_DB", Path.Combine(dataDir, "telemetry.db"))
    .WithEnvironment("DUCK_COUNT", "5")
    .WithEnvironment("SQUEAK_MIN_DELAY_MS", "2")
    .WithEnvironment("SQUEAK_MAX_DELAY_MS", "6")
    .WithEnvironment("LOUD_DUCK_ID", "duck-1")
    .WithEnvironment("RUN_SIMULATOR", "true");

builder.AddProject<Projects.DuckNet_AlarmCenter>("alarm")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("DUCKNET_DB", Path.Combine(dataDir, "alarm.db"))
    .WithEnvironment("ALARM_RATE_THRESHOLD", "10")
    .WithEnvironment("ALARM_WINDOW_SECONDS", "60")
    .WithEnvironment("EVENT_LOG_URL", telemetry.GetEndpoint("http"))
    .WithEnvironment("SHARD_COUNT", "3")
    .WithEnvironment("HANDLE_DELAY_MS", "12")
    .WaitFor(telemetry);

builder.AddProject<Projects.DuckNet_BillingCenter>("billing")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("DUCKNET_DB", Path.Combine(dataDir, "billing.db"))
    .WithEnvironment("EVENT_LOG_URL", telemetry.GetEndpoint("http"))
    .WithEnvironment("SAGA_TIMEOUT_SECONDS", "15")
    .WithEnvironment("BILLING_FEE_CENTS", "100")
    .WithEnvironment("SHARD_COUNT", "3")
    .WaitFor(telemetry);

builder.AddProject<Projects.DuckNet_DashboardCenter>("dashboard")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("DUCKNET_DB", Path.Combine(dataDir, "dashboard.db"))
    .WithEnvironment("EVENT_LOG_URL", telemetry.GetEndpoint("http"))
    .WithEnvironment("SHARD_COUNT", "3")
    .WithEnvironment("HANDLE_DELAY_MS", "12")
    .WaitFor(telemetry);

builder.Build().Run();
