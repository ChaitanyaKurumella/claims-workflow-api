using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Claims.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

public sealed class ExportTests
{
    sealed class Collector : HttpMessageHandler
    {
        public ConcurrentBag<(string Path, byte[] Body)> Requests { get; } = [];
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Content!.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult()));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsByteArrayAsync(ct)));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }

    [Fact]
    public async Task Opt_in_exports_real_otlp_without_request_secrets()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Claims:TelemetryEnabled"] = "true" }).Build();
        var collector = new Collector();
        var services = new ServiceCollection().AddLogging();
        foreach (var signal in new[] { "traces", "metrics" })
            services.Configure<OtlpExporterOptions>("claims-" + signal, options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = new Uri("http://collector.invalid:4318/v1/" + signal);
                options.HttpClientFactory = () => new HttpClient(collector, disposeHandler: false);
            });
        services.AddClaimsObservability(config);
        using var provider = services.BuildServiceProvider();
        var traces = provider.GetRequiredService<TracerProvider>();
        var metrics = provider.GetRequiredService<MeterProvider>();
        using var telemetry = new RequestTelemetry(NullLogger<RequestTelemetry>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/private-customer-123";
        context.Request.QueryString = new("?token=secret-token-123");
        context.Request.Headers.Authorization = "Bearer secret-credential-123";
        await telemetry.InvokeAsync(context, c => { c.Response.StatusCode = 503; return Task.CompletedTask; });
        OperationsTelemetry.RecordDelivery("failed", 42);
        Assert.True(traces.ForceFlush());
        Assert.True(metrics.ForceFlush());
        Assert.Contains(collector.Requests, x => x.Path == "/v1/traces" && x.Body.Length > 0);
        Assert.Contains(collector.Requests, x => x.Path == "/v1/metrics" && x.Body.Length > 0);
        var wire = string.Join("", collector.Requests.Select(x => Encoding.UTF8.GetString(x.Body)));
        Assert.Contains("claims.request", wire);
        Assert.Contains("claims.http.requests", wire);
        Assert.Contains("claims.delivery.attempts", wire);
        Assert.DoesNotContain("private-customer-123", wire);
        Assert.DoesNotContain("secret-token-123", wire);
        Assert.DoesNotContain("secret-credential-123", wire);
    }

    [Fact]
    public void Disabled_telemetry_registers_no_export_providers()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddClaimsObservability(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<TracerProvider>());
        Assert.Null(provider.GetService<MeterProvider>());
    }
}
