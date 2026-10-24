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
            // The routes are mapped only while app-scoped trusted documents are enabled; without this every path would 404 vacuously.
            builder.UseSetting("Gateway:GraphQL:AppScopedTrustedDocuments:Enabled", "true");
            builder.UseSetting("Gateway:GraphQL:PersistedQueriesOnly", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    private HttpClient CreateClient(string? roles, string? scope = null)
    {
        var client = _factory.CreateClient();
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-caller");
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
            client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
            if (scope is not null)
            {
                client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
            }
        }

        return client;
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Admin_CanonicalPath_Anonymous401_NonAdmin403_ClusterAdminIsMapped()
    {
        using var anonymous = CreateClient(null);
        using var nonAdmin = CreateClient("User");
        using var admin = CreateClient("ClusterAdmin", "trusted-documents:read");

        using var anonymousResponse = await anonymous.GetAsync("/api/admin/trusted-documents/app-1");
        using var nonAdminResponse = await nonAdmin.GetAsync("/api/admin/trusted-documents/app-1");
        using var adminResponse = await admin.GetAsync("/api/admin/trusted-documents/app-1");

        ((int)anonymousResponse.StatusCode).ShouldBe(401);
        ((int)nonAdminResponse.StatusCode).ShouldBe(403);
        // Positive control: the route is mapped, so the 401/403 above and the variant rejections below are not a blanket 404.
        ((int)adminResponse.StatusCode).ShouldNotBe(404);
        ((int)adminResponse.StatusCode).ShouldNotBe(401);
        ((int)adminResponse.StatusCode).ShouldNotBe(403);
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
