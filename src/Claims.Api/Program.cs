using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Claims.Api;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Diagnostics.Metrics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
var publicDemo = builder.Configuration.GetValue<bool>("Claims:PublicDemo");
var demo = builder.Configuration.GetValue<bool>("Claims:DemoMode");
if (publicDemo) builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16384);
var identities = new List<(byte[] Hash, Actor Actor)>();
foreach (var role in new[]
{
    "Reviewer",
    "Supervisor",
    "Administrator"
}

)
{
    var secret = builder.Configuration[$"Claims:{role}Key"] ?? (role == "Administrator" ? builder.Configuration["Claims:ApiKey"] : null);
    if (string.IsNullOrEmpty(secret))
        continue;
    if (secret.Length < 24)
        throw new InvalidOperationException($"Claims:{role}Key needs at least 24 characters.");
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    if (identities.Any(x => CryptographicOperations.FixedTimeEquals(x.Hash, hash)))
        throw new InvalidOperationException("Role keys must differ.");
    identities.Add((hash, new(role.ToLowerInvariant() + "-01", role)));
}

var authority = builder.Configuration["Claims:Authority"];
var audience = builder.Configuration["Claims:Audience"];
if (authority is not null)
{
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer) || issuer.Scheme != "https" || string.IsNullOrWhiteSpace(audience))
        throw new InvalidOperationException("OIDC requires an HTTPS authority and audience.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.Authority = authority;
        o.Audience = audience;
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "sub",
            RoleClaimType = "roles",
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
    builder.Services.AddAuthorization();
}

if (identities.Count == 0 && authority is null && !publicDemo)
    throw new InvalidOperationException("Configure Claims__ReviewerKey, Claims__SupervisorKey and Claims__AdministratorKey.");
if (publicDemo && (!demo || authority is not null || identities.Count > 0 || builder.Configuration["Claims:DeliveryUrl"] is { Length: > 0 }))
    throw new InvalidOperationException("Public demo requires demo mode, no service keys, no OIDC and no external delivery.");
if (publicDemo)
{
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<DemoWorkspaces>();
    builder.Services.AddScoped(sp => (Store)sp.GetRequiredService<IHttpContextAccessor>().HttpContext!.Items["demo-store"]!);
}
else
    builder.Services.AddSingleton(new Store(builder.Configuration["Claims:DataPath"] ?? "data/claims-v2.db"));
if (builder.Configuration["Claims:DeliveryUrl"] is { Length: > 0 } deliveryUrl)
{
    if (!Uri.TryCreate(deliveryUrl, UriKind.Absolute, out var target) || (target.Scheme != "https" && !(target.Scheme == "http" && target.IsLoopback)))
        throw new InvalidOperationException("DeliveryUrl requires HTTPS, or HTTP on loopback for testing.");
    builder.Services.AddHttpClient<IEventDelivery, HttpEventDelivery>(c => c.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddSingleton(sp => sp.GetRequiredService<Store>().Deliveries);
    builder.Services.AddTransient<OutboxDispatcher>();
}

if (!publicDemo) builder.Services.AddHostedService<OperationsWorker>();
var demoSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, Actor>();
if (authority is not null && (demo || identities.Count > 0))
    throw new InvalidOperationException("OIDC mode cannot be combined with demo sessions or service role keys.");
var app = builder.Build();
if (!publicDemo && demo && builder.Configuration.GetValue<bool>("Claims:SeedDemo"))
    DemoData.Seed(app.Services.GetRequiredService<Store>());
using var meter = new Meter("Claims.Operations", "2.0.0");
var requests = meter.CreateCounter<long>("claims.http.requests");
var duration = meter.CreateHistogram<double>("claims.http.duration", "ms");
app.UseExceptionHandler();
// Render terminates TLS. Trust only the scheme, never forwarded host or client identity.
if (publicDemo)
{
    var forwarded = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
    app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (!context.Request.IsHttps && !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None))
        {
            context.Response.StatusCode = 400;
            return;
        }
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/demo/session"))
            await context.RequestServices.GetRequiredService<DemoWorkspaces>().Invoke(context, next);
        else await next(context);
    });
}
if (authority is not null)
    app.UseAuthentication();
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
            if (authority is not null && context.User.Identity?.IsAuthenticated == true)
            {
                var subject = context.User.FindFirst("sub")?.Value;
                var roles = context.User.FindAll("roles").Select(x => x.Value).Where(x => x is "Reviewer" or "Supervisor" or "Administrator").Distinct().ToArray();
                // Ambiguous role mappings fail closed. Subject comes only from a validated token.
                if (!string.IsNullOrWhiteSpace(subject) && roles.Length == 1)
                    actor = new(subject, roles[0]);
                else
                {
                    context.Response.StatusCode = 403;
                    return;
                }
            }

            if (actor is null && demo && context.Request.Cookies["claims-demo"] is { } token)
            {
                if (publicDemo && context.Items["demo-workspace"] is DemoWorkspace workspace)
                    actor = new(workspace.Role.ToLowerInvariant() + "-01", workspace.Role);
                else demoSessions.TryGetValue(token, out actor);
            }
            if (actor is null)
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (context.Request.Method != "GET" && context.Request.Headers.Origin is { Count: > 0 } origin && origin.ToString() != $"{context.Request.Scheme}://{context.Request.Host}")
            {
                context.Response.StatusCode = 403;
                return;
            }

            context.Items["actor"] = actor;
            if (context.Request.Path.StartsWithSegments("/api/operations") && actor.Role == "Reviewer")
            {
                context.Response.StatusCode = 403;
                return;
            }
        }

        try
        {
            await next();
        }
        catch (RuleException e)
        {
            await Results.Problem(e.Message, statusCode: e.Status, extensions: new Dictionary<string, object?> { { "requestId", context.TraceIdentifier } }).ExecuteAsync(context);
        }
    }
    finally
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        requests.Add(1, new KeyValuePair<string, object?>("status", context.Response.StatusCode));
        duration.Record(elapsed);
        app.Logger.LogInformation("Request {RequestId} {Method} {Path} returned {Status} in {ElapsedMs}ms", context.TraceIdentifier, context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsed);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/demo/config", () => Results.Ok(new { enabled = demo }));
app.MapPost("/demo/session/{role}", (string role, HttpContext c) =>
{
    if (!demo || (!publicDemo && (c.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote))) || role is not ("Reviewer" or "Supervisor" or "Administrator"))
        return Results.NotFound();
    if (c.Request.Headers.Origin.ToString() != $"{c.Request.Scheme}://{c.Request.Host}")
        return Results.StatusCode(403);
    if (publicDemo)
    {
        ((DemoWorkspace)c.Items["demo-workspace"]!).Role = role;
        return Results.Ok(new { role });
    }
    if (c.Request.Cookies["claims-demo"] is { } previous)
        demoSessions.TryRemove(previous, out _);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    demoSessions[token] = new(role.ToLowerInvariant() + "-01", role);
    c.Response.Cookies.Append("claims-demo", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps, MaxAge = TimeSpan.FromHours(2) });
    return Results.Ok(new { role });
});
app.MapGet("/health", (IServiceProvider services) =>
{
    if (!publicDemo) services.GetRequiredService<Store>().CheckHealth();
    return Results.Ok(new { status = "healthy", storage = "sqlite", schema = SchemaMigrator.CurrentVersion });
});
app.MapGet("/api/session", (HttpContext c) => Results.Ok(c.Items["actor"]));
app.MapGet("/api/claims", (Store s) => Results.Ok(s.QueryClaims(null, null, 100)));
app.MapGet("/api/claims/page", (Store s, string? status, string? after, int? limit) =>
{
    if (limit is < 1 or > 100)
        throw new RuleException(400, "Page size must be 1–100.");
    ClaimStatus? state = null;
    if (status is not null)
    {
        if (!Enum.TryParse<ClaimStatus>(status, out var parsed) || !Enum.IsDefined(parsed))
            throw new RuleException(400, "Invalid status.");
        state = parsed;
    }

    var size = limit ?? 50;
    var rows = s.QueryClaims(state, after, size + 1);
    return Results.Ok(new { items = rows.Take(size), nextCursor = rows.Length > size ? rows[size - 1].Id.ToString() : null });
});
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
    var summary = s.Summary();
    var events = s.Outbox();
    var deliveries = s.DeliveryCounts();
    return Results.Ok(new { summary, deliveryMode = builder.Configuration["Claims:DeliveryUrl"] is { Length: > 0 } ? "HTTP" : "Local", pendingEvents = deliveries.Pending, deadLetters = deliveries.DeadLetters, events });
});
app.MapPost("/api/operations/outbox/{id:long}/retry", (long id, Store s) =>
{
    s.Retry(id);
    return Results.Ok(new { retried = id });
});
app.Run();
public partial class Program
{
}
