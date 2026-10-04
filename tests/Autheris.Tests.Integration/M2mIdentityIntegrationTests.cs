using System.Net;
using System.Net.Http.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public class M2mIdentityIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public M2mIdentityIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "500");
        });
    }

    private HttpClient CreateM2mClient(string appId = "spn-etl-batch-01", string? tenant = "tenant-alpha")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-AppId", appId);
        client.DefaultRequestHeaders.Add("X-Test-IdTyp", "app");
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        }
        return client;
    }

    [Fact]
    public async Task M2M_ServicePrincipal_WithConsent_CanQueryData_AndMaskingIsApplied()
    {
        const string appId = "spn-etl-batch-01";
        var client = CreateM2mClient(appId);

        // Seed an active consent for this ServicePrincipal on finance.dbo.finance_table_1
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var meta = await repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
            meta.ShouldNotBeNull();

            var consent = new Consent
            {
                TableId = meta.Table.Id,
                TableIdentifier = meta.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.ServicePrincipal,
                GranteeSid = new Sid(appId),
                TenantId = new TenantId("tenant-alpha"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
                }
            };
            await repo.CreateConsentAsync(consent);
        }

        var query = new
        {
            query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 5) { tableName totalCount jsonRows } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("FORBIDDEN");
        body.ShouldContain("finance.dbo.finance_table_1");
        body.ShouldContain("totalCount");
        body.ShouldContain("jsonRows");
        // Verify email column was masked
        body.ShouldContain("u***@");
        body.ShouldNotContain("user1@corp.local");
    }

    [Fact]
    public async Task M2M_ServicePrincipal_WithoutConsent_ReturnsForbidden()
    {
        var client = CreateM2mClient("spn-unauthorized-service");

        var query = new
        {
            query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 5) { tableName totalCount jsonRows } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("FORBIDDEN");
        body.ShouldContain("Zero Trust");
    }

    [Fact]
    public async Task M2M_ServicePrincipal_CanQueryOData_WithConsent()
    {
        const string appId = "spn-powerbi-gateway";
        var client = CreateM2mClient(appId);

        // Seed an active consent for this ServicePrincipal on finance.dbo.finance_table_1
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var meta = await repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
            meta.ShouldNotBeNull();

            var consent = new Consent
            {
                TableId = meta.Table.Id,
                TableIdentifier = meta.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.ServicePrincipal,
                GranteeSid = new Sid(appId),
                TenantId = new TenantId("tenant-alpha"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
                }
            };
            await repo.CreateConsentAsync(consent);
        }

        var response = await client.GetAsync("/odata/v4/finance/dbo/finance_table_1?$top=5");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("@odata.context");
        body.ShouldContain("u***@");
        body.ShouldNotContain("user1@corp.local");
    }
}
