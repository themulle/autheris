namespace Autheris.Tests.Integration.Usage;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

public sealed class UsageReportEndpointsTests : IClassFixture<UsageReportEndpointsTests.HostFixture>
{
    private readonly HostFixture _factory;

    public UsageReportEndpointsTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("UsageReportEndpointsTests")
            });
        }
    }

    private HttpClient CreateClient(string? roles, string sid = "S-1-5-21-100-200-300-1001", string tenant = "tenant-default")
    {
        var client = _factory.CreateClient();
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
            client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        }

        return client;
    }

    [Theory]
    [InlineData("/api/v1/usage/me")]
    [InlineData("/api/v1/usage/users")]
    [InlineData("/api/v1/usage/groups")]
    [InlineData("/api/v1/usage/tenant")]
    [InlineData("/api/v1/usage/tables/default.sales.orders")]
    [InlineData("/api/v1/usage/sources")]
    [InlineData("/api/v1/usage/applications")]
    [InlineData("/api/v1/usage/grants/00000000-0000-0000-0000-000000000001")]
    public async Task Anonymous_usage_report_get_returns_401(string path)
    {
        var res = await CreateClient(null).GetAsync(path);
        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tenant_admin_can_query_json_and_csv_usage_reports_with_no_store_headers()
    {
        var admin = CreateClient("TenantAdmin");

        var jsonRes = await admin.GetAsync("/api/v1/usage/users");
        jsonRes.StatusCode.ShouldBe(HttpStatusCode.OK);
        jsonRes.Headers.CacheControl?.NoStore.ShouldBeTrue();
        using var doc = await JsonDocument.ParseAsync(await jsonRes.Content.ReadAsStreamAsync());
        doc.RootElement.TryGetProperty("rows", out _).ShouldBeTrue();

        var csvRes = await admin.GetAsync("/api/v1/usage/groups?format=csv");
        csvRes.StatusCode.ShouldBe(HttpStatusCode.OK);
        csvRes.Headers.CacheControl?.NoStore.ShouldBeTrue();
        csvRes.Content.Headers.ContentDisposition?.DispositionType.ShouldBe("attachment");
        (await csvRes.Content.ReadAsStringAsync()).ShouldStartWith("# multi-counted: a user in N groups is counted in each group");
    }

    [Fact]
    public async Task Invalid_range_returns_400_usage_report_invalid_range()
    {
        var admin = CreateClient("TenantAdmin");
        var res = await admin.GetAsync("/api/v1/usage/sources?from=2026-11-10&to=2026-11-01");
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).ShouldContain(ApiErrorCodes.UsageReportInvalidRange);
    }
}
