using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Claims.Api;

public static class OperationsTelemetry
{
    public const string Name = "Claims.Operations";
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("claims.delivery.attempts");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("claims.delivery.duration", "ms");

    public static void RecordDelivery(string outcome, double milliseconds)
    {
        var tags = new TagList { { "outcome", outcome } };
        Deliveries.Add(1, tags);
        Duration.Record(milliseconds, tags);
    }

    public static IServiceCollection AddClaimsObservability(this IServiceCollection services, IConfiguration config)
    {
        // Explicit opt-in; no collector traffic or exporter credentials by default.
        if (!config.GetValue<bool>("Claims:TelemetryEnabled")) return services;
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("claimdesk-api"))
            .WithTracing(tracing => tracing.AddSource(Name).AddOtlpExporter("claims-traces", _ => { }))
            .WithMetrics(metrics => metrics.AddMeter(Name).AddOtlpExporter("claims-metrics", _ => { }));
        return services;
    }
}
