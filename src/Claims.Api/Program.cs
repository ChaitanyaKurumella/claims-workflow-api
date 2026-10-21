using Claims.Api;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
var key = builder.Configuration["Claims:ApiKey"];
if (string.IsNullOrWhiteSpace(key) || key.Length < 24)
    throw new InvalidOperationException("Set Claims__ApiKey to a random secret of at least 24 characters.");
var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
var store = new Store(builder.Configuration["Claims:DataPath"] ?? "data/claims.json");
var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if(context.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = context.Request.Headers["X-Api-Key"].ToString();
        if(!CryptographicOperations.FixedTimeEquals(keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        { context.Response.StatusCode = 401; return; }
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/claims", () => Results.Ok(store.List()));
app.MapGet("/api/claims/{id:guid}", (Guid id) => store.Get(id) is {} item ? Results.Ok(item.Claim) : Results.NotFound());
app.MapGet("/api/claims/{id:guid}/history", (Guid id) => store.Get(id) is {} item ? Results.Ok(item.History) : Results.NotFound());
app.MapPost("/api/claims", (CreateClaim input) =>
{
    var error = ClaimRules.Validate(input);
    if(error is not null) return Results.Problem(error, statusCode:400);
    var claim = store.Create(input);
    return claim is null ? Results.Problem("Reference already exists.", statusCode:409)
        : Results.Created($"/api/claims/{claim.Id}", claim);
});
app.MapPost("/api/claims/{id:guid}/decision", (Guid id, Decision input) =>
{
    if(input.Outcome is not ("approve" or "reject")) return Results.Problem("Outcome must be approve or reject.", statusCode:400);
    var result = store.Decide(id, input.Outcome);
    return result.Status switch { 200 => Results.Ok(result.Claim), 404 => Results.NotFound(),
        _ => Results.Problem("A final decision already exists.", statusCode:409) };
});
app.Run();
public partial class Program { }
