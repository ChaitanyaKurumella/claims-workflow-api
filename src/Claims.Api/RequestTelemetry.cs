using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Claims.Api;

// Wrap the exception handler so metrics and completion logs see its final status.
public sealed class RequestTelemetry : IMiddleware, IDisposable
{
    private static readonly object TraceKey = new();
    public static string? GetTraceId(HttpContext context) =>
        context.Items[TraceKey] as string ?? Activity.Current?.TraceId.ToString();

    private readonly Meter meter = new("Claims.Operations", "3.0.0");
    private readonly Counter<long> requests;
    private readonly Histogram<double> duration;
    private readonly ILogger<RequestTelemetry> logger;

    public RequestTelemetry(ILogger<RequestTelemetry> logger)
    {
        this.logger = logger;
        requests = meter.CreateCounter<long>("claims.http.requests");
        duration = meter.CreateHistogram<double>("claims.http.duration", "ms");
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // ASP.NET Core supplies the W3C server activity and validates traceparent.
        var traceId = Activity.Current?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString();
        context.Items[TraceKey] = traceId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
            context.Response.Headers["X-Trace-Id"] = traceId;
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestId"] = context.TraceIdentifier,
            ["TraceId"] = traceId
        });
        using var activity = OperationsTelemetry.Activities.StartActivity("claims.request", ActivityKind.Internal);
        activity?.SetTag("claims.request.trace_id", traceId);
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var status = failed ? 500 : context.Response.StatusCode;
            // Route templates avoid recording claim IDs, query strings or attacker-controlled paths.
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var method = context.Request.Method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
                ? context.Request.Method : "OTHER";
            var tags = new TagList { { "http.request.method", method }, { "http.route", route }, { "http.response.status_code", status } };
            activity?.SetTag("http.request.method", method);
            activity?.SetTag("http.route", route);
            activity?.SetTag("http.response.status_code", status);
            if (status >= 500) activity?.SetStatus(ActivityStatusCode.Error);
            requests.Add(1, tags);
            duration.Record(elapsed, tags);
            logger.LogInformation("Request {RequestId} trace {TraceId} {Method} {Route} returned {Status} in {ElapsedMs}ms",
                context.TraceIdentifier, traceId, method, route, status, elapsed);
        }
    }

    public void Dispose() => meter.Dispose();
}
