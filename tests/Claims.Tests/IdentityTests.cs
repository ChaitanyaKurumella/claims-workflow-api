using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

public sealed class IdentityTests
{
    const string Issuer = "https://identity.example";
    static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-signing-key-at-least-sixty-four-characters-long-for-hmac-sha256"));
    sealed class JwtApp : WebApplicationFactory<Program>
    {
        readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseSetting("Claims:Authority", Issuer).UseSetting("Claims:Audience", "claimdesk").UseSetting("Claims:DisableWorker", "true").UseSetting("Claims:DataPath", Path.Combine(folder, "jwt.db"));
            b.ConfigureServices(s => s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer
                };
                config.SigningKeys.Add(Key);
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    static string Token(string issuer = Issuer, string audience = "claimdesk", bool expired = false, string[]? roles = null, bool wrongSignature = false)
    {
        var claims = new List<Claim>
        {
            new("sub", "person-alice")
        };
        claims.AddRange((roles ?? ["Reviewer"]).Select(r => new Claim("roles", r)));
        var signing = wrongSignature ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string ('x', 64))) : Key;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims, DateTime.UtcNow.AddHours(-2), expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5), new SigningCredentials(signing, SecurityAlgorithms.HmacSha256)));
    }

    [Fact]
    public async Task Validated_subject_is_personal_identity_and_reviewers_cannot_read_operations()
    {
        await using var app = new JwtApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token());
        var session = await client.GetStringAsync("/api/session");
        Assert.Contains("person-alice", session);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/operations")).StatusCode);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task Invalid_tokens_are_rejected(string mode)
    {
        await using var app = new JwtApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(issuer: mode == "issuer" ? "https://evil.example" : Issuer, audience: mode == "audience" ? "other" : "claimdesk", expired: mode == "expired", wrongSignature: mode == "signature"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Reviewer,Supervisor")]
    public async Task Unknown_or_ambiguous_role_mapping_fails_closed(string roles)
    {
        await using var app = new JwtApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(roles: roles.Split(',')));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/session")).StatusCode);
    }
}
