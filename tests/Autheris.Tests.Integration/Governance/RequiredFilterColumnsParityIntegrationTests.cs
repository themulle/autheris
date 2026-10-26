namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #163 (PLAN-GOV-163): Table-level required filter columns enforced uniformly across every read path.
/// Verifies that all transports (WebSQL, GraphQL, OData, Arrow Export) reject queries missing required filter columns
/// with HTTP 400 and the stable error code REQUIRED_FILTER_COLUMN_MISSING.
/// </summary>
[Trait("Category", "Security")]
public sealed class RequiredFilterColumnsParityIntegrationTests : IClassFixture<RequiredFilterColumnsParityIntegrationTests.RequiredFilterFixture>
{
    private static readonly TableIdentifier Air1Id = new("default", "main", "air1");
    private const string ExpectedErrorCode = "REQUIRED_FILTER_COLUMN_MISSING";

    private readonly RequiredFilterFixture _fixture;

    public RequiredFilterColumnsParityIntegrationTests(RequiredFilterFixture fixture) => _fixture = fixture;

    public sealed class RequiredFilterFixture : RowFilterChannelParityTests.Fixture
    {
        protected override void ConfigureHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokenBucketCapacity", "100000");
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokensPerSecond", "10000");
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:MaxCostPerMinute", "1000000");
        }

        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            var air1 = (await repo.GetTableMetadataAsync(Air1Id))!;
            await repo.UpsertTableMetadataAsync(air1 with
            {
                Table = air1.Table with { RequiredFilterColumns = ["value"] }
            });
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WebSql_MissingRequiredFilter_RejectedWith400AndStableCode()
    {
        var response = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id FROM default.main.air1" });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        code.ShouldBe(ExpectedErrorCode);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OData_MissingRequiredFilter_RejectedWith400AndStableCode()
    {
        var response = await _fixture.Client().GetAsync("/odata/v4/default/main/air1?$select=id");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var raw = doc.RootElement.GetRawText();
        raw.ShouldContain(ExpectedErrorCode);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ArrowExport_MissingRequiredFilter_RejectedWith400AndStableCode()
    {
        var response = await _fixture.Client().PostAsync("/api/v1/export/arrow?table=default.main.air1", content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var raw = doc.RootElement.GetRawText();
        raw.ShouldContain(ExpectedErrorCode);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQl_MissingRequiredFilter_ReturnsErrorWithStableCode()
    {
        using var doc = await GraphQlQueryAsync("{ default_main_air1(first: 10) { id } }");
        var root = doc.RootElement;
        root.TryGetProperty("errors", out var errors).ShouldBeTrue();
        errors.GetArrayLength().ShouldBeGreaterThan(0);
        var raw = errors.GetRawText();
        raw.ShouldContain(ExpectedErrorCode);
    }

    private async Task<JsonDocument> GraphQlQueryAsync(string query)
    {
        var c = _fixture.Client();
        c.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        var r = await c.PostAsJsonAsync("/graphql", new { query });
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync());
    }
}
