using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Claims.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class TelemetryTests
{
    [Fact]
    public async Task Unexpected_exception_returns_correlated_problem_response()
    {
        using var host = await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(services => services.AddSingleton<RequestTelemetry>().AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier;
                context.ProblemDetails.Extensions["traceId"] = RequestTelemetry.GetTraceId(context.HttpContext);
            }))
            .Configure(app =>
            {
                app.UseMiddleware<RequestTelemetry>();
                app.UseExceptionHandler();
                app.Run(_ => throw new InvalidOperationException("injected failure"));
            })).StartAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/fault");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Trace-Id")), body.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Request-Id")), body.RootElement.GetProperty("requestId").GetString());
        Assert.DoesNotContain("injected failure", body.RootElement.ToString());
    }

    [Theory]
    [InlineData("/api/session", true, HttpStatusCode.OK)]
    [InlineData("/api/session", false, HttpStatusCode.Unauthorized)]
    [InlineData("/api/claims/page?limit=101", true, HttpStatusCode.BadRequest)]
    [InlineData("/api/claims/00000000-0000-0000-0000-000000000001", true, HttpStatusCode.NotFound)]
    public async Task Responses_preserve_w3c_trace_and_problem_correlation(string path, bool authenticated, HttpStatusCode status)
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        if (!authenticated) client.DefaultRequestHeaders.Remove("X-Api-Key");
        const string traceId = "0123456789abcdef0123456789abcdef";
        client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-0123456789abcdef-01");
        using var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(traceId, Assert.Single(response.Headers.GetValues("X-Trace-Id")));
        var requestId = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        if (status == HttpStatusCode.BadRequest)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(traceId, body.RootElement.GetProperty("traceId").GetString());
            Assert.Equal(requestId, body.RootElement.GetProperty("requestId").GetString());
        }
    }

    [Fact]
    public async Task Malformed_traceparent_is_replaced()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("traceparent", "untrusted-invalid-value");
        using var response = await client.GetAsync("/health");
        var trace = Assert.Single(response.Headers.GetValues("X-Trace-Id"));
        Assert.Equal(32, trace.Length);
        Assert.True(ActivityTraceId.CreateFromString(trace).ToHexString() == trace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metrics_record_final_failure_status_and_route_template_without_sensitive_dimensions(bool throws)
    {
        var observations = new List<(string Name, Dictionary<string, object?> Tags)>();
        RequestDelegate next = context =>
        {
            if (throws) throw new InvalidOperationException("test failure");
            context.Response.StatusCode = 503;
            return Task.CompletedTask;
        };
        using var telemetry = new RequestTelemetry(NullLogger<RequestTelemetry>.Instance);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Claims.Operations") meterListener.EnableMeasurementEvents(instrument);
        };
        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            // Other xUnit classes may run servers concurrently. Only inspect our route.
            var values = tags.ToArray().ToDictionary(x => x.Key, x => x.Value);
            if (values.GetValueOrDefault("http.route") as string == "/telemetry-test/{id:guid}")
                observations.Add((instrument.Name, values));
        }
        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.Start();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/telemetry-test/secret-claim-id";
        context.Request.QueryString = new QueryString("?token=private");
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/telemetry-test/{id:guid}"), 0, EndpointMetadataCollection.Empty, "test"));
        if (throws) await Assert.ThrowsAsync<InvalidOperationException>(() => telemetry.InvokeAsync(context, next));
        else await telemetry.InvokeAsync(context, next);
        Assert.Equal(2, observations.Count);
        Assert.Contains(observations, x => x.Name == "claims.http.requests");
        Assert.Contains(observations, x => x.Name == "claims.http.duration");
        Assert.All(observations, observation =>
        {
            Assert.Equal(throws ? 500 : 503, observation.Tags["http.response.status_code"]);
            Assert.Equal("POST", observation.Tags["http.request.method"]);
            Assert.Equal(3, observation.Tags.Count);
            Assert.DoesNotContain(observation.Tags.Values, value => value?.ToString()?.Contains("private") == true);
        });
    }
}
