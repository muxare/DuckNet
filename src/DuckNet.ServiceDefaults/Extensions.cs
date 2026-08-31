using DuckNet.EventBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Aspire-style service defaults, trimmed to OpenTelemetry.
/// HTTP resilience stays on <c>PollingLoop</c>; <c>/health</c> stays Center-owned
/// so we do not remap Aspire's default health endpoints over existing routes.
/// </summary>
public static class Extensions
{
    public const string LabCorsPolicy = "DuckNetLab";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.Services.AddCors(options =>
        {
            options.AddPolicy(LabCorsPolicy, policy =>
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
        });
        return builder;
    }

    /// <summary>
    /// Lab CORS so the Dashboard Vue app can poll other Centers as a browser client.
    /// Not a Center-to-Center call.
    /// </summary>
    public static WebApplication UseDuckNetLabCors(this WebApplication app)
    {
        app.UseCors(LabCorsPolicy);
        return app;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new AlwaysOnSampler());
                tracing.AddSource(builder.Environment.ApplicationName);
                foreach (var source in DuckNetTracing.SourceNames)
                {
                    tracing.AddSource(source);
                }

                tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();
        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
