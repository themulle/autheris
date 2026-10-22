namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class EntraBearerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly SymmetricSecurityKey TestKey = new(Encoding.UTF8.GetBytes("a-very-secret-test-key-of-at-least-256-bits-length!"));
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";

    private readonly WebApplicationFactory<Program> _factory;

    public EntraBearerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:Authentication:Entra:TenantId", TenantId);
            builder.UseSetting("Gateway:Authentication:Entra:ClientId", ClientId);
            builder.UseSetting("Gateway:Authentication:Entra:Audience", ClientId);

            builder.ConfigureServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.IssuerSigningKey = TestKey;
                });
            });
        });
    }

    private static string CreateSignedToken(IEnumerable<Claim> claims)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(TestKey, SecurityAlgorithms.HmacSha256Signature),
            Expires = DateTime.UtcNow.AddHours(1)
        };
        var token = tokenHandler.CreateToken(descriptor);
        return tokenHandler.WriteToken(token);
    }

    [Fact]
    public async Task EntraBearer_SignedIdTokenWithoutNonce_Returns401()
    {
        // An ID token: carries roles and subject, but no 'azp' or 'appid', and no 'nonce'
        var idToken = CreateSignedToken(new[]
        {
            new Claim("sub", "user-id-123"),
            new Claim("oid", "33333333-3333-3333-3333-333333333333"),
            new Claim("preferred_username", "alice@example.com"),
            new Claim("roles", "Reader")
        });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        var response = await client.GetAsync("/api/v1/catalog/tables");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
