using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string Key = "test-only-secret-at-least-24-characters";
    public const string ReviewerKey = "reviewer-test-secret-at-least-24-characters";
    private readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Claims:ApiKey", Key).UseSetting("Claims:ReviewerKey", ReviewerKey).UseSetting("Claims:DisableWorker", "true").UseSetting("Claims:DataPath", Path.Combine(folder, "claims.json"));
    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }
}
