using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class WorkflowTests
{
    [Theory]
    [InlineData("", 10)]
    [InlineData("DEMO", 0)]
    [InlineData("DEMO", -1)]
    [InlineData("DEMO", 1000001)]
    [InlineData("DEMO", 1.001)]
    public async Task Invalid_claims_are_rejected(string reference, decimal amount)
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/claims", new { reference, amount });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Claim_can_be_decided_once_and_read_back()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        var created = await client.PostAsJsonAsync("/api/claims", new { reference = "DEMO-001", amount = 125.50m });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location!;
        var approved = await client.PostAsJsonAsync($"{location}/decision", new { outcome = "approve" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var repeated = await client.PostAsJsonAsync($"{location}/decision", new { outcome = "reject" });
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        var stored = await client.GetFromJsonAsync<JsonElement>(location);
        Assert.Equal("Approved", stored.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Missing_claim_returns_not_found()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/claims/{Guid.NewGuid()}")).StatusCode);
    }
}
