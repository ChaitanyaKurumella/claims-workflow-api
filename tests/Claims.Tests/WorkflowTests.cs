using Claims.Api;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
public sealed class WorkflowTests
{
    static readonly Actor Reviewer = new("reviewer-01", "Reviewer"), Supervisor = new("supervisor-01", "Supervisor");
    [Theory]
    [InlineData("", 10)]
    [InlineData("DEMO", 0)]
    [InlineData("DEMO", -1)]
    [InlineData("DEMO", 1000001)]
    [InlineData("DEMO", 1.001)]
    public async Task Invalid_claims_are_rejected(string reference, decimal amount)
    { await using var app = new TestApp(); using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString()); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/claims", new { reference, amount })).StatusCode); }
    [Fact]
    public async Task Api_enforces_version_assignment_and_rationale()
    {
        await using var app = new TestApp(); using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("Idempotency-Key", "workflow-test-key");
        var response = await client.PostAsJsonAsync("/api/claims", new { reference = "API-001", amount = 100 }); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var c = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/claims/{c}/approve", new { version = 1, note = "checked" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/claims/{c}/assign", new { version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/claims/{c}/approve", new { version = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/claims/{c}/approve", new { version = 2, note = "Evidence verified" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/claims/{c}/reject", new { version = 3, note = "changed mind" })).StatusCode);
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/claims/{c}/history"); Assert.Equal(3, history.GetArrayLength());
    }
    [Fact]
    public async Task Api_roles_are_derived_from_secret_and_operations_forbid_reviewers()
    {
        await using var app = new TestApp(); using var client = app.CreateClient(); client.DefaultRequestHeaders.Clear();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/claims")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApp.ReviewerKey);
        client.DefaultRequestHeaders.Add("X-Role", "Administrator");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/operations")).StatusCode);
        var s = await client.GetFromJsonAsync<JsonElement>("/api/session"); Assert.Equal("Reviewer", s.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/demo/session/Administrator", new { })).StatusCode);
    }
    [Fact]
    public async Task Missing_claim_returns_not_found()
    { await using var app = new TestApp(); using var client = app.CreateClient(); Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/claims/{Guid.NewGuid()}")).StatusCode); }

    [Theory]
    [InlineData("Unknown", "Normal", "summary")]
    [InlineData("Medical", "Invalid", "summary")]
    [InlineData("Medical", "Normal", null)]
    public async Task Invalid_case_metadata_returns_bad_request(string category, string priority, string? description)
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/claims", new { reference = "META", amount = 10, category, priority, description });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
