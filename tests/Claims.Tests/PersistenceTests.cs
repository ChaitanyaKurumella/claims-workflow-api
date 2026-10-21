using Claims.Api;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

public sealed class PersistenceTests
{
    [Fact]
    public void Restart_preserves_claim_and_audit_and_competing_decisions_have_one_winner()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try {
            var path = Path.Combine(folder,"claims.json");
            var store = new Store(path);
            var claim = store.Create(new("DEMO-RESTART", 100))!;
            var results = new System.Collections.Concurrent.ConcurrentBag<int>();
            Parallel.For(0, 20, i => results.Add(store.Decide(claim.Id, i % 2 == 0 ? "approve" : "reject").Status));
            Assert.Single(results, x => x == 200);
            Assert.Equal(19, results.Count(x => x == 409));
            var reopened = new Store(path);
            Assert.Equal(2, reopened.Get(claim.Id)!.History.Length);
            Assert.NotEqual(ClaimStatus.Submitted, reopened.Get(claim.Id)!.Claim.Status);
            Assert.Null(reopened.Create(new("demo-restart", 10)));
        } finally { if(Directory.Exists(folder)) Directory.Delete(folder,true); }
    }
    [Fact]
    public async Task Api_requires_key_and_history_records_decision()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Clear();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/claims")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApp.Key);
        var created = await client.PostAsJsonAsync("/api/claims", new {reference="DEMO-AUDIT", amount=10});
        var location = created.Headers.Location!;
        await client.PostAsJsonAsync($"{location}/decision", new {outcome="reject"});
        var history = await client.GetFromJsonAsync<JsonElement>($"{location}/history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("Rejected", history[1].GetProperty("action").GetString());
    }
}
