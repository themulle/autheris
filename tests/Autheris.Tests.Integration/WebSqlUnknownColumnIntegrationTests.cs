namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
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

    private async Task<(HttpStatusCode Status, string Body)> PostSqlAsync(string sql)
    {
        var client = _factory.CreateClient();
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
    public async Task HiddenColumnTable_UnknownAndHidden_SameBody_RealPostgres()
    {
        // the caller holds Clear rules for id, note and tenant_id only: ssn is hidden from this caller
        await RegisterAsync("ledger", ["id", "note", "ssn", "tenant_id"], clearColumns: ["id", "note", "tenant_id"]);

        var unknown = await PostSqlAsync("SELECT nope FROM ledger");
        var hidden = await PostSqlAsync("SELECT ssn FROM ledger");

        using var doc = JsonDocument.Parse(unknown.Body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("SQL_POLICY_DENIED");
        unknown.Status.ShouldBe(hidden.Status);
        WithoutTrace(unknown.Body).ShouldBe(WithoutTrace(hidden.Body));   // hidden equals non-existent: no name, no position, no hint
        foreach (var key in new[] { "line", "column", "hint", "columnName" })
        {
            doc.RootElement.TryGetProperty(key, out _).ShouldBeFalse(key);
        }
    }
}
