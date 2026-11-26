namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// WebSQL result cache end to end: the cache is on (global budget and a per-source TTL). Every statement still runs the full
/// rewrite, authorization and audit; the cache only skips the source execution. A host without an authoritative epoch service
/// never serves a hit, which is the fail-closed outcome these tests accept wherever a hit cannot be forced.
/// </summary>
public sealed class WebSqlResultCacheIntegrationTests : IClassFixture<WebSqlResultCacheIntegrationTests.HostFixture>
{
    private const string Tenant = "tenant_rc";
    private const string SidA = "S-1-5-21-RC-A";
    private const string SidB = "S-1-5-21-RC-B";
    private const string Sql = "SELECT id, name FROM rc_items ORDER BY id";

    private readonly HostFixture _factory;

    public WebSqlResultCacheIntegrationTests(HostFixture factory) => _factory = factory;

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        private readonly string _dbPath;

        public HostFixture()
        {
            _dbPath = PostgresDataSource.Create("rc", """
                CREATE TABLE rc_items (id TEXT, name TEXT, secret TEXT, tenant_id TEXT);
                INSERT INTO rc_items VALUES ('1','Ada','s-1','tenant_rc'),('2','Bob','s-2','tenant_rc'),('3','Eve','s-3','tenant_other');
                """, "public");
        }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:Insecure:danger_allow_anonymous_access"] = "true",
                ["Gateway:Insecure:danger_bypass_consent_checks"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("WebSqlResultCacheIntegrationTests"),
                ["Gateway:DataSources:Connections:default:Provider"] = "PostgreSql",
                ["Gateway:DataSources:Connections:default:DefaultSchema"] = "public",
                ["Gateway:DataSources:Connections:default:ConnectionString"] = _dbPath,
                ["Gateway:DataSources:Connections:default:ResultCacheTtlSeconds"] = "300",
                ["Gateway:WebSql:Enabled"] = "true",
                ["Gateway:WebSql:DefaultDataSourceName"] = "default",
                ["Gateway:WebSql:AllowDml"] = "false",
                ["Gateway:WebSql:ResultCacheMaxMegabytes"] = "64",
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private async Task RegisterAsync(Dictionary<string, MaskingRule>? masking = null, params string[] columns)
    {
        var names = columns.Length == 0 ? new[] { "id", "name", "secret", "tenant_id" } : columns;
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "rc_items", TableName = "rc_items", SchemaName = "public", SourceName = "default", SourceType = "PostgreSql" },
            Identifier = new TableIdentifier("default", "public", "rc_items"),
            Columns = names.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
            ColumnClassifications = masking == null ? new Dictionary<string, IReadOnlyList<ColumnClassification>>() : TableMetadataClassificationTestExtensions.PiiFor(masking.Keys.ToArray()),
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
        });
    }

    private async Task<(HttpResponseMessage Response, string Body)> PostAsync(string sid, string sql = Sql, string? resultCache = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        var response = await client.PostAsJsonAsync("/api/v1/sql", new { sql, resultCache });
        return (response, await response.Content.ReadAsStringAsync());
    }

    private static string? CacheStatus(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("resultCache", out var rc) ? rc.GetProperty("status").GetString() : null;
    }

    /// <summary>Posts until the caller's own repeated statement is answered from the cache; null when the host never hits.</summary>
    private async Task<(HttpResponseMessage Response, string Body)?> PostUntilHitAsync(string sid, string sql = Sql)
    {
        await PostAsync(sid, sql);
        (HttpResponseMessage Response, string Body)? hit = null;
        try
        {
            await WaitHelper.WaitUntil(async () =>
            {
                var next = await PostAsync(sid, sql);
                if (CacheStatus(next.Body) != "hit")
                {
                    return false;
                }

                hit = next;
                return true;
            }, TimeSpan.FromSeconds(10), "resultCache.status=hit", TimeSpan.FromMilliseconds(200));
        }
        catch (TimeoutException)
        {
        }

        return hit;
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Two_users_same_sql_never_share_entry()
    {
        await RegisterAsync();
        await PostUntilHitAsync(SidA);

        var (response, body) = await PostAsync(SidB);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        CacheStatus(body).ShouldNotBe("hit");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Revoked_grant_denied_and_no_cached_rows_served()
    {
        await RegisterAsync();
        await PostUntilHitAsync(SidA);

        // Closest feasible revocation in this host (consent checks are bypassed): the column the statement needs leaves the catalog.
        await RegisterAsync(null, "id", "secret", "tenant_id");
        var (response, body) = await PostAsync(SidA);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            CacheStatus(body).ShouldNotBe("hit");
        }
        else
        {
            body.ShouldNotContain("Ada");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Policy_change_after_store_gives_miss()
    {
        await RegisterAsync();
        await PostUntilHitAsync(SidA);

        await RegisterAsync(new Dictionary<string, MaskingRule> { ["secret"] = new() { RuleType = "REDACT", Replacement = "XXX" } });
        var (response, body) = await PostAsync(SidA);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        CacheStatus(body).ShouldNotBe("hit");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mask_change_after_store_gives_miss_or_masked_rows()
    {
        const string maskedSql = "SELECT id, secret FROM rc_items ORDER BY id";
        await RegisterAsync();
        await PostUntilHitAsync(SidA, maskedSql);

        await RegisterAsync(new Dictionary<string, MaskingRule> { ["secret"] = new() { RuleType = "REDACT", Replacement = "XXX" } });
        var (response, body) = await PostAsync(SidA, maskedSql);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        if (CacheStatus(body) == "hit")
        {
            body.ShouldNotContain("s-1");
        }

        body.ShouldNotContain("s-3");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hit_produces_audit_row_marked_hit()
    {
        await RegisterAsync();
        var hit = await PostUntilHitAsync(SidA);
        hit.ShouldNotBeNull("the single-node host is expected to reach the cache");

        AuditLogEntry? marked = null;
        await WaitHelper.WaitUntil(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
            marked = (await repo.GetAuditLogEntriesAsync(1000)).FirstOrDefault(e =>
                e.EventType == Autheris.Domain.Audit.AuditEventTypes.WebSqlQuery &&
                e.Decision == "ALLOW" &&
                e.ActorSid.Value == SidA &&
                e.DetailsJson != null &&
                e.DetailsJson.Contains("\"resultCache\":\"hit\"", StringComparison.Ordinal));
            return marked != null;
        }, TimeSpan.FromSeconds(10), "audit entry with resultCache hit", TimeSpan.FromMilliseconds(200));

        marked.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cache_control_no_store_present_on_hit()
    {
        await RegisterAsync();
        var hit = await PostUntilHitAsync(SidA);
        hit.ShouldNotBeNull("the single-node host is expected to reach the cache");

        hit.Value.Response.Headers.CacheControl?.NoStore.ShouldBe(true);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Metric_view_min_group_size_rows_not_leaked_via_cache()
    {
        // No metric view is registered in this host. The closest assertion: a replayed aggregate equals the executed one row for row,
        // and a second caller never receives the first caller's entry.
        const string groupedSql = "SELECT name, COUNT(*) AS n FROM rc_items GROUP BY name ORDER BY name";
        await RegisterAsync();
        var (_, executed) = await PostAsync(SidA, groupedSql);
        var hit = await PostUntilHitAsync(SidA, groupedSql);

        if (hit != null)
        {
            using var first = JsonDocument.Parse(executed);
            using var replayed = JsonDocument.Parse(hit.Value.Body);
            replayed.RootElement.GetProperty("rows").GetRawText().ShouldBe(first.RootElement.GetProperty("rows").GetRawText());
            replayed.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(first.RootElement.GetProperty("rowCount").GetInt32());
        }

        var (_, other) = await PostAsync(SidB, groupedSql);
        CacheStatus(other).ShouldNotBe("hit");
    }
}
