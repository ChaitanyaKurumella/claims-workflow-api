using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Claims.Api;
using Xunit;

public sealed class PublicDemoTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class App(Clock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseSetting("Claims:PublicDemo", "true").UseSetting("Claims:DemoMode", "true");
            b.ConfigureServices(s => s.AddSingleton(new DemoWorkspaces(clock)));
        }
    }
    static HttpClient Client(App app)
    {
        var c = app.CreateClient(new() { BaseAddress = new Uri("https://demo.example"), HandleCookies = true });
        c.DefaultRequestHeaders.Add("Origin", "https://demo.example");
        return c;
    }
    [Fact]
    public async Task Visitors_are_isolated_roles_preserve_workspace_and_expiry_resets_data()
    {
        var clock = new Clock();
        await using var app = new App(clock);
        using var a = Client(app);
        using var b = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/claims")).StatusCode);
        (await a.PostAsync("/demo/session/Reviewer", null)).EnsureSuccessStatusCode();
        (await b.PostAsync("/demo/session/Reviewer", null)).EnsureSuccessStatusCode();
        a.DefaultRequestHeaders.Add("Idempotency-Key", "isolation-test-01");
        var created = await a.PostAsJsonAsync("/api/claims", new { reference = "PRIVATE-TEST", amount = 100, category = "Travel", description = "Visitor isolation test", priority = "Normal" });
        created.EnsureSuccessStatusCode();
        var claim = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/claims/{claim.GetProperty("id").GetGuid()}")).StatusCode);
        (await a.PostAsync("/demo/session/Supervisor", null)).EnsureSuccessStatusCode();
        Assert.Contains("PRIVATE-TEST", await a.GetStringAsync("/api/claims"));
        (await a.GetAsync("/api/operations")).EnsureSuccessStatusCode();
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/claims")).StatusCode);
        (await a.PostAsync("/demo/session/Reviewer", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain("PRIVATE-TEST", await a.GetStringAsync("/api/claims"));
    }
    [Fact]
    public async Task Capacity_and_mutation_limits_fail_closed()
    {
        await using var app = new App(new Clock());
        using var first = Client(app);
        (await first.PostAsync("/demo/session/Reviewer", null)).EnsureSuccessStatusCode();
        for (var i = 0; i < 199; i++)
            (await first.PostAsync("/demo/session/Reviewer", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.PostAsync("/demo/session/Reviewer", null)).StatusCode);
        for (var i = 1; i < 32; i++)
        {
            using var visitor = Client(app);
            (await visitor.PostAsync("/demo/session/Reviewer", null)).EnsureSuccessStatusCode();
        }
        using var overflow = Client(app);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await overflow.PostAsync("/demo/session/Reviewer", null)).StatusCode);
    }
    [Fact]
    public async Task Proxy_https_cookie_and_cross_origin_rejection_work()
    {
        await using var app = new App(new Clock());
        using var c = app.CreateClient(new() { BaseAddress = new Uri("http://demo.example"), HandleCookies = false });
        c.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        c.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/demo/session/Reviewer", null)).StatusCode);
        c.DefaultRequestHeaders.Remove("Origin");
        c.DefaultRequestHeaders.Add("Origin", "https://demo.example");
        var response = await c.PostAsync("/demo/session/Reviewer", null);
        response.EnsureSuccessStatusCode();
        Assert.Contains("secure", response.Headers.GetValues("Set-Cookie").Single());
        Assert.Contains("httponly", response.Headers.GetValues("Set-Cookie").Single());
    }
}
