using Claims.Api;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseExceptionHandler();
var claims = new ConcurrentDictionary<Guid, Claim>();
// Serialize transitions so competing decisions cannot both succeed.
var gate = new object();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/claims", () => Results.Ok(claims.Values.OrderBy(c => c.Reference)));
app.MapGet("/api/claims/{id:guid}", (Guid id) => claims.TryGetValue(id, out var claim)
    ? Results.Ok(claim) : Results.NotFound());
app.MapPost("/api/claims", (CreateClaim input) =>
{
    var error = ClaimRules.Validate(input);
    if (error is not null) return Results.Problem(error, statusCode: 400);
    var claim = new Claim(Guid.NewGuid(), input.Reference!.Trim(), input.Amount, ClaimStatus.Submitted);
    claims[claim.Id] = claim;
    return Results.Created($"/api/claims/{claim.Id}", claim);
});
app.MapPost("/api/claims/{id:guid}/decision", (Guid id, Decision input) =>
{
    if (input.Outcome is not ("approve" or "reject"))
        return Results.Problem("Outcome must be approve or reject.", statusCode: 400);
    lock (gate)
    {
        if (!claims.TryGetValue(id, out var claim)) return Results.NotFound();
        if (!ClaimRules.CanDecide(claim))
            return Results.Problem("A final decision already exists.", statusCode: 409);
        var updated = claim with { Status = input.Outcome == "approve" ? ClaimStatus.Approved : ClaimStatus.Rejected };
        claims[id] = updated;
        return Results.Ok(updated);
    }
});
app.Run();
public partial class Program { }
