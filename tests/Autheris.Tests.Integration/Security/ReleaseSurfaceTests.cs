namespace Autheris.Tests.Integration.Security;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ReleaseSurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReleaseSurfaceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Gateway:Hosting:AllowedHosts:0", "localhost");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:GovernanceDb:AuditHmacKeyVaultRef", "audit-hmac-key");
            builder.UseSetting("Gateway:GovernanceDb:AuditHmacKey", "0123456789012345678901234567890123456789");
            builder.UseSetting("Gateway:DataMasking:HmacSecretKeyVaultRef", "kv://masking-hmac");
            builder.UseSetting("Gateway:Audit:ChainAnchorPath", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anchor-" + Guid.NewGuid().ToString("N")));
            builder.UseSetting("Gateway:RestApi:BulkStream:SigningKey", "0123456789012345678901234567890123456789");
            builder.UseSetting("Gateway:DuckDbOlap:MemoryBudgetBytes", "2147483648");
        });
    }

    [Theory]
    [InlineData("GET", "/api/dev/info")]
    [InlineData("GET", "/api/dev/personas")]
    [InlineData("GET", "/api/dev/login/admin")]
    public async Task Dev_endpoints_absent_outside_development(string method, string path)
    {
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Endpoint {method} {path} must return 404 in Production");
    }

    [Fact]
    public void EndpointDataSource_DoesNotContain_DevEndpoints_InProduction()
    {
        using var client = _factory.CreateClient();
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var devEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith("/api/dev", StringComparison.OrdinalIgnoreCase))
            .ToList();

        devEndpoints.ShouldBeEmpty("No /api/dev/* routes should be mapped in Production");
    }
}
