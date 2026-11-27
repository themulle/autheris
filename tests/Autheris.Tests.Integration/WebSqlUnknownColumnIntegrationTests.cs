namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// An unknown column is named only when the caller sees every column of the table; a hidden column is answered exactly like an unknown one.
/// Runs on the host of <see cref="GovernedWebSqlIntegrationTests"/> (real PostgreSQL, executed on CI).
/// </summary>
public sealed class WebSqlUnknownColumnIntegrationTests : IClassFixture<GovernedWebSqlIntegrationTests.HostFixture>
{
    private readonly GovernedWebSqlIntegrationTests.HostFixture _factory;

    public WebSqlUnknownColumnIntegrationTests(GovernedWebSqlIntegrationTests.HostFixture factory) => _factory = factory;

    private async Task<(HttpStatusCode Status, string Body)> PostSqlAsync(string sql, WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-WEBSQL");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_alpha");
        var response = await client.PostAsJsonAsync("/api/v1/sql", new { sql });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string WithoutTrace(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return string.Join("|", doc.RootElement.EnumerateObject().Where(p => p.Name is not ("traceId" or "correlationId")).Select(p => p.Name + "=" + p.Value));
    }

    private async Task RegisterAsync(string table, string[] columns, string[]? clearColumns)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var id = new TableIdentifier("default", "public", table);
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = table, TableName = table, SchemaName = "public", SourceName = "default", SourceType = "PostgreSql" },
            Identifier = id,
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
        });
        if (clearColumns is null)
        {
            return;
        }

        var meta = await repo.GetTableMetadataAsync(id);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = meta!.Table.Id,
            TableIdentifier = id,
            TenantId = new TenantId("tenant_alpha"),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-WEBSQL"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = clearColumns.Select(c => new ConsentColumnRule { ColumnName = c, AccessLevel = ColumnAccessLevel.Clear }).ToList()
        });
    }

    // WithWebHostBuilder re-runs the fixture's host configuration, which clones a brand-new (empty) governance database. The derived host would
    // therefore not see the table metadata and consent registered above and fail closed. It is pinned to the fixture's governance database instead.
    private WebApplicationFactory<Program> EnforcingHost()
    {
        var governance = _factory.Services.GetRequiredService<IConfiguration>()["Gateway:GovernanceDb:ConnectionString"]
            ?? throw new InvalidOperationException("The shared host has no governance connection string.");
        var overrides = new Dictionary<string, string?>
        {
            ["Gateway:Insecure:danger_bypass_consent_checks"] = "false",
            ["Gateway:GovernanceDb:ConnectionString"] = governance,
        };
        return _factory.WithWebHostBuilder(b =>
        {
            foreach (var pair in overrides)
            {
                b.UseSetting(pair.Key, pair.Value);
            }

            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(overrides));
        });
    }

    [Fact]
    public async Task UnknownColumn_RealPostgres_NamedWhenFullyVisible()
    {
        await RegisterAsync("customers", ["id", "name", "email", "active", "tenant_id"], clearColumns: null);

        var (status, body) = await PostSqlAsync("SELECT nme FROM customers");

        status.ShouldBe(HttpStatusCode.BadRequest, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("UNKNOWN_COLUMN");
        doc.RootElement.GetProperty("columnName").GetString().ShouldBe("nme");
        doc.RootElement.GetProperty("line").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("column").GetInt32().ShouldBe(8);
        doc.RootElement.GetProperty("hint").GetString()!.ShouldContain("'nme'");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task HiddenColumnTable_ProjectionIsNullified_FilterAnswersLikeUnknown_RealPostgres()
    {
        // The shared host bypasses consent checks, so every column is visible there and nothing can be hidden. This host enforces consents:
        // the caller holds Clear rules for id, note and tenant_id only, so ssn is a Deny column for this caller (seeded value 111-11-1111).
        await RegisterAsync("ledger", ["id", "note", "ssn", "tenant_id"], clearColumns: ["id", "note", "tenant_id"]);
        await using var enforcing = EnforcingHost();

        var allowed = await PostSqlAsync("SELECT id, note FROM ledger", enforcing);   // control: the consent is honoured on this host
        allowed.Status.ShouldBe(HttpStatusCode.OK, allowed.Body);

        // Projection: a Deny column that is only projected is nullified by design, never returned.
        var projected = await PostSqlAsync("SELECT ssn FROM ledger", enforcing);
        projected.Status.ShouldBe(HttpStatusCode.OK, projected.Body);
        projected.Body.ShouldNotContain("111-11-1111");
        using (var doc = JsonDocument.Parse(projected.Body))
        {
            var ssn = doc.RootElement.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "ssn");
            ssn.GetProperty("masked").GetBoolean().ShouldBeTrue(projected.Body);
            var values = doc.RootElement.GetProperty("rows").EnumerateArray()
                .SelectMany(r => r.ValueKind == JsonValueKind.Object ? r.EnumerateObject().Select(p => p.Value) : r.EnumerateArray())
                .ToList();
            values.ShouldNotBeEmpty(projected.Body);
            values.ShouldAllBe(v => v.ValueKind == JsonValueKind.Null, projected.Body);
        }

        // Filter: a Deny column in a predicate is refused, and the refusal must not tell it apart from a column that does not exist.
        // If these two ever differ, that is a distinguishability finding tracked by #1278.
        var unknown = await PostSqlAsync("SELECT id FROM ledger WHERE nope = 'x'", enforcing);
        var hidden = await PostSqlAsync("SELECT id FROM ledger WHERE ssn = 'x'", enforcing);

        unknown.Status.ShouldBe(HttpStatusCode.Forbidden, unknown.Body);
        hidden.Status.ShouldBe(unknown.Status, hidden.Body);
        using (var doc = JsonDocument.Parse(unknown.Body))
        {
            doc.RootElement.GetProperty("code").GetString().ShouldBe("SQL_POLICY_DENIED");
            foreach (var key in new[] { "line", "column", "hint", "columnName" })
            {
                doc.RootElement.TryGetProperty(key, out _).ShouldBeFalse(key);
            }
        }

        WithoutTrace(hidden.Body).ShouldBe(WithoutTrace(unknown.Body));   // hidden equals non-existent: same code, no name, no position, no hint
        foreach (var body in new[] { unknown.Body, hidden.Body })
        {
            body.ShouldNotContain("nope");
            body.ShouldNotContain("ssn");
        }
    }
}
