namespace Autheris.Tests.Integration;

using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-281-6-C5: the ingress deny rule is defense in depth only; the application must reject unauthenticated and non-admin
/// callers on every path variant of the admin API (case, trailing slash, encoded slash, double slash). There is no fallback
/// route that could answer 2xx for these paths.
/// </summary>
public sealed class AdminPathVariantRejectionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminPathVariantRejectionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("/api/admin/trusted-documents")]
    [InlineData("/API/ADMIN/trusted-documents")]
    [InlineData("/api/admin/trusted-documents/")]
    [InlineData("/api%2Fadmin/trusted-documents")]
    [InlineData("/api//admin/trusted-documents")]
    public async Task Admin_PathVariants_AnonymousAndNonAdmin_NeverSucceed(string path)
    {
        using var anonymous = _factory.CreateClient();
        using var nonAdmin = _factory.CreateClient();
        nonAdmin.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-user");
        nonAdmin.DefaultRequestHeaders.Add("X-Test-Roles", "User");
        nonAdmin.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");

        foreach (var client in new[] { anonymous, nonAdmin })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, path);
                using var response = await client.SendAsync(request);

                new[] { 401, 403, 404, 405 }.ShouldContain((int)response.StatusCode, $"{method} {path} must never succeed");
            }
        }
    }
}
