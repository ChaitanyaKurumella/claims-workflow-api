using System.Security.Cryptography;

namespace Claims.Api;

public sealed class DemoWorkspace(Store store, string path, DateTimeOffset expires)
{
    public Store Store { get; } = store;
    public string Path { get; } = path;
    public DateTimeOffset Expires { get; } = expires;
    public string Role { get; set; } = "Reviewer";
    public int Mutations { get; set; }
}

// One bounded gate keeps expiry, file cleanup and requests from racing each other.
public sealed class DemoWorkspaces : IDisposable
{
    readonly SemaphoreSlim gate = new(1);
    readonly Dictionary<string, DemoWorkspace> workspaces = new();
    readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claimdesk-sandboxes-" + Guid.NewGuid().ToString("N"));
    readonly TimeProvider clock;
    readonly Timer cleanup;
    public DemoWorkspaces() : this(TimeProvider.System) { }
    public DemoWorkspaces(TimeProvider clock)
    {
        this.clock = clock;
        cleanup = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    void Expire()
    {
        foreach (var pair in workspaces.Where(x => x.Value.Expires <= clock.GetUtcNow()).ToArray())
        {
            Directory.Delete(pair.Value.Path, true);
            workspaces.Remove(pair.Key);
        }
    }
    void Sweep()
    {
        if (!gate.Wait(0)) return;
        try { Expire(); } finally { gate.Release(); }
    }
    public async Task Invoke(HttpContext context, RequestDelegate next)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted))
        {
            context.Response.StatusCode = 429;
            return;
        }
        try
        {
            Expire();
            var token = context.Request.Cookies["claims-demo"];
            DemoWorkspace? workspace = null;
            if (token is not null) workspaces.TryGetValue(token, out workspace);
            var starting = context.Request.Path.StartsWithSegments("/demo/session") && context.Request.Method == "POST";
            if (starting && (context.Request.Headers.Origin.ToString() != $"{context.Request.Scheme}://{context.Request.Host}" ||
                context.Request.Path.Value?.Split('/').Last() is not ("Reviewer" or "Supervisor" or "Administrator")))
            {
                context.Response.StatusCode = 403;
                return;
            }
            if (workspace is null && starting)
            {
                if (workspaces.Count >= 32) { context.Response.StatusCode = 429; return; }
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var folder = System.IO.Path.Combine(root, token);
                var store = new Store(System.IO.Path.Combine(folder, "claims.db"));
                DemoData.Seed(store);
                workspace = new(store, folder, clock.GetUtcNow().AddMinutes(30));
                workspaces.Add(token, workspace);
                context.Response.Cookies.Append("claims-demo", token, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = context.Request.IsHttps,
                    MaxAge = TimeSpan.FromMinutes(30),
                    Path = "/"
                });
            }
            if (workspace is null) { context.Response.StatusCode = 401; return; }
            if (context.Request.Method != "GET" && ++workspace.Mutations > 200)
            {
                context.Response.StatusCode = 429;
                return;
            }
            context.Items["demo-store"] = workspace.Store;
            context.Items["demo-workspace"] = workspace;
            workspace.Store.Escalate(clock.GetUtcNow());
            for (var i = 0; i < 50 && workspace.Store.DispatchOne(clock.GetUtcNow(), false); i++) { }
            await next(context);
        }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        cleanup.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        gate.Dispose();
    }
}
