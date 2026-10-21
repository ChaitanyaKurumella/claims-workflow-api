using Claims.Api;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Diagnostics.Metrics;
var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
var identities = new List<(byte[] Hash, Actor Actor)>();
foreach (var role in new[] { "Reviewer", "Supervisor", "Administrator" })
{
    var secret = builder.Configuration[$"Claims:{role}Key"] ?? (role == "Administrator" ? builder.Configuration["Claims:ApiKey"] : null);
    if (string.IsNullOrEmpty(secret)) continue;
    if (secret.Length < 24) throw new InvalidOperationException($"Claims:{role}Key needs at least 24 characters.");
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    if (identities.Any(x => CryptographicOperations.FixedTimeEquals(x.Hash, hash))) throw new InvalidOperationException("Role keys must differ.");
    identities.Add((hash, new(role.ToLowerInvariant() + "-01", role)));
}
if (identities.Count == 0) throw new InvalidOperationException("Configure Claims__ReviewerKey, Claims__SupervisorKey and Claims__AdministratorKey.");
builder.Services.AddSingleton(new Store(builder.Configuration["Claims:DataPath"] ?? "data/claims-v2.db"));
builder.Services.AddHostedService<OperationsWorker>();
var demo = builder.Configuration.GetValue<bool>("Claims:DemoMode");
var demoSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, Actor>();
var app = builder.Build();
if (demo && builder.Configuration.GetValue<bool>("Claims:SeedDemo")) DemoData.Seed(app.Services.GetRequiredService<Store>());
using var meter = new Meter("Claims.Operations", "2.0.0");
var requests = meter.CreateCounter<long>("claims.http.requests");
var duration = meter.CreateHistogram<double>("claims.http.duration", "ms");
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    try
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Api-Key"].ToString()));
            var actor = identities.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(x.Hash, hash)).Actor;
            if (actor is null && demo && context.Request.Cookies["claims-demo"] is { } token) demoSessions.TryGetValue(token, out actor);
            if (actor is null) { context.Response.StatusCode = 401; return; }
            if (context.Request.Method != "GET" && context.Request.Headers.Origin is { Count: > 0 } origin && origin.ToString() != $"{context.Request.Scheme}://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
            context.Items["actor"] = actor;
            if (context.Request.Path.StartsWithSegments("/api/operations") && actor.Role == "Reviewer") { context.Response.StatusCode = 403; return; }
        }
        try { await next(); }
        catch (RuleException e) { await Results.Problem(e.Message, statusCode: e.Status, extensions: new Dictionary<string, object?> { { "requestId", context.TraceIdentifier } }).ExecuteAsync(context); }
    }
    finally
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        requests.Add(1, new KeyValuePair<string, object?>("status", context.Response.StatusCode)); duration.Record(elapsed);
        app.Logger.LogInformation("Request {RequestId} {Method} {Path} returned {Status} in {ElapsedMs}ms", context.TraceIdentifier, context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsed);
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/demo/config", () => Results.Ok(new { enabled = demo }));
app.MapPost("/demo/session/{role}", (string role, HttpContext c) =>
{
    if (!demo || c.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote) || role is not ("Reviewer" or "Supervisor" or "Administrator")) return Results.NotFound();
    if (c.Request.Headers.Origin.ToString() != $"{c.Request.Scheme}://{c.Request.Host}") return Results.StatusCode(403);
    if (c.Request.Cookies["claims-demo"] is { } previous) demoSessions.TryRemove(previous, out _);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    demoSessions[token] = new(role.ToLowerInvariant() + "-01", role);
    c.Response.Cookies.Append("claims-demo", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps, MaxAge = TimeSpan.FromHours(2) });
    return Results.Ok(new { role });
});
app.MapGet("/health", (Store store) => { store.List(); return Results.Ok(new { status = "healthy", storage = "sqlite", schema = 1 }); });
app.MapGet("/api/session", (HttpContext c) => Results.Ok(c.Items["actor"]));
app.MapGet("/api/claims", (Store s) => Results.Ok(s.List()));
app.MapGet("/api/claims/{id:guid}", (Guid id, Store s) => s.Get(id) is { } x ? Results.Ok(x.Claim) : Results.NotFound());
app.MapGet("/api/claims/{id:guid}/history", (Guid id, Store s) => s.Get(id) is { } x ? Results.Ok(x.History) : Results.NotFound());
app.MapPost("/api/claims", (CreateClaim input, HttpContext c, Store s) =>
{
    var result = s.Create(input, (Actor)c.Items["actor"]!, c.Request.Headers["Idempotency-Key"].ToString());
    c.Response.Headers["Idempotency-Replayed"] = result.Replayed.ToString();
    return result.Replayed ? Results.Ok(result.Claim) : Results.Created($"/api/claims/{result.Claim.Id}", result.Claim);
});
app.MapPost("/api/claims/{id:guid}/{action}", (Guid id, string action, Command input, HttpContext c, Store s) => Results.Ok(s.Apply(id, action, input, (Actor)c.Items["actor"]!)));
app.MapGet("/api/operations", (Store s) =>
{
    var all = s.List(); var events = s.Outbox(); var deliveries = s.DeliveryCounts(); var now = DateTimeOffset.UtcNow;
    return Results.Ok(new { total = all.Length, exposure = all.Where(x => x.Status is not (ClaimStatus.Approved or ClaimStatus.Rejected)).Sum(x => x.Amount), overdue = all.Count(x => x.DueAt < now && x.Status is not (ClaimStatus.Approved or ClaimStatus.Rejected)), pendingEvents = deliveries.Pending, deadLetters = deliveries.DeadLetters, events });
});
app.MapPost("/api/operations/outbox/{id:long}/retry", (long id, Store s) => { s.Retry(id); return Results.Ok(new { retried = id }); });
app.Run();
public partial class Program { }
