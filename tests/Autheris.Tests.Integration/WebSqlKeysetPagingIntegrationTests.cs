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
/// WebSQL keyset continuation end to end (PostgreSQL). With <c>Paging:MaxOffset=20</c> and a page size of 10, pages 1 to 3 are offset pages
/// and the continuation from page 3 on is a keyset token, so a statement ordered by exactly the declared primary key reads to the end.
/// Every page still runs the full governed pipeline.
/// </summary>
public sealed class WebSqlKeysetPagingIntegrationTests : IClassFixture<WebSqlKeysetPagingIntegrationTests.HostFixture>
{
    private const string Tenant = "tenant_ks";
    private const int PageSize = 10;

    private readonly HostFixture _factory;

    public WebSqlKeysetPagingIntegrationTests(HostFixture factory) => _factory = factory;

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        private readonly string _dbPath;

        public HostFixture()
        {
            _dbPath = PostgresDataSource.Create("ks", """
                CREATE TABLE ks_items (id INTEGER PRIMARY KEY, name TEXT, tenant_id TEXT);
                INSERT INTO ks_items SELECT g, 'n' || (100 - g), 'tenant_ks' FROM generate_series(1, 75) g;

                CREATE TABLE ks_comp (region TEXT, day DATE, tenant_id TEXT, PRIMARY KEY (region, day));
                INSERT INTO ks_comp SELECT 'r' || r, DATE '2024-01-01' + d, 'tenant_ks'
                  FROM generate_series(1, 4) r, generate_series(0, 19) d;

                CREATE TABLE ks_uuid (id UUID PRIMARY KEY, tenant_id TEXT);
                INSERT INTO ks_uuid SELECT ('00000000-0000-0000-0000-' || lpad(g::text, 12, '0'))::uuid, 'tenant_ks' FROM generate_series(1, 75) g;

                CREATE TABLE ks_ts (ts TIMESTAMP PRIMARY KEY, n INTEGER, tenant_id TEXT);
                INSERT INTO ks_ts SELECT TIMESTAMP '2024-01-01 00:00:00' + (g * INTERVAL '1 second'), g, 'tenant_ks' FROM generate_series(1, 75) g;

                CREATE TABLE ks_mixed (id INTEGER PRIMARY KEY, tenant_id TEXT);
                INSERT INTO ks_mixed SELECT g, CASE WHEN g % 3 = 0 THEN 'tenant_ks_other' ELSE 'tenant_ks' END FROM generate_series(1, 90) g;

                CREATE TABLE ks_masked (id TEXT PRIMARY KEY, tenant_id TEXT);
                INSERT INTO ks_masked SELECT lpad(g::text, 3, '0'), 'tenant_ks' FROM generate_series(1, 75) g;

                CREATE TABLE ks_live (id INTEGER PRIMARY KEY, tenant_id TEXT);
                INSERT INTO ks_live SELECT g * 2, 'tenant_ks' FROM generate_series(1, 75) g;

                CREATE TABLE ks_cache (id INTEGER PRIMARY KEY, tenant_id TEXT);
                INSERT INTO ks_cache SELECT g, 'tenant_ks' FROM generate_series(1, 75) g;
                """, "public");
        }

        public string ConnectionString => _dbPath;

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:Insecure:danger_allow_anonymous_access"] = "true",
                ["Gateway:Insecure:danger_bypass_consent_checks"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("WebSqlKeysetPagingIntegrationTests"),
                ["Gateway:DataSources:Connections:default:Provider"] = "PostgreSql",
                ["Gateway:DataSources:Connections:default:DefaultSchema"] = "public",
                ["Gateway:DataSources:Connections:default:ConnectionString"] = _dbPath,
                ["Gateway:DataSources:Connections:default:ResultCacheTtlSeconds"] = "300",
                ["Gateway:WebSql:Enabled"] = "true",
                ["Gateway:WebSql:DefaultDataSourceName"] = "default",
                ["Gateway:WebSql:AllowDml"] = "false",
                ["Gateway:WebSql:ResultCacheMaxMegabytes"] = "64",
                ["Gateway:WebSql:Paging:MaxOffset"] = "20",
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private sealed record Column(string Name, string DataType);

    private sealed record PageResult(HttpStatusCode Status, string Body)
    {
        public JsonDocument Json => JsonDocument.Parse(Body);

        public string? Next
        {
            get
            {
                using var doc = Json;
                return doc.RootElement.TryGetProperty("next", out var n) ? n.GetString() : null;
            }
        }

        public string? PagingUnavailable
        {
            get
            {
                using var doc = Json;
                return doc.RootElement.TryGetProperty("pagingUnavailable", out var n) ? n.GetString() : null;
            }
        }

        public (string? Status, string? Reason) ResultCache
        {
            get
            {
                using var doc = Json;
                if (!doc.RootElement.TryGetProperty("resultCache", out var rc))
                {
                    return (null, null);
                }

                return (rc.GetProperty("status").GetString(), rc.TryGetProperty("reason", out var r) ? r.GetString() : null);
            }
        }

        /// <summary>The first column of every row as text (rows are objects by default, arrays with format=arrays).</summary>
        public List<string> FirstColumn()
        {
            using var doc = Json;
            var names = doc.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();
            return doc.RootElement.GetProperty("rows").EnumerateArray()
                .Select(row => row.ValueKind == JsonValueKind.Array ? row[0] : row.GetProperty(names[0]))
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText())
                .ToList();
        }
    }

    private async Task RegisterAsync(string table, string[] primaryKey, Column[] columns, Dictionary<string, MaskingRule>? masking = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = table, TableName = table, SchemaName = "public", SourceName = "default", SourceType = "PostgreSql" },
            Identifier = new TableIdentifier("default", "public", table),
            PrimaryKeyColumns = primaryKey,
            ReplaceKeyDefinition = true,
            Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.DataType }).ToList(),
            ColumnClassifications = masking == null ? new Dictionary<string, IReadOnlyList<ColumnClassification>>() : TableMetadataClassificationTestExtensions.PiiFor(masking.Keys.ToArray()),
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
        });
    }

    private Task RegisterItemsAsync(string table = "ks_items") =>
        RegisterAsync(table, ["id"], [new("id", "integer"), new("tenant_id", "varchar")]);

    private async Task<PageResult> PostAsync(string sid, string sql, string? next = null, string resultCache = "bypass", string tenant = Tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        object body = next is null
            ? new { sql, page = new { size = PageSize }, resultCache }
            : new { sql, next, resultCache };
        var response = await client.PostAsJsonAsync("/api/v1/sql", body);
        return new PageResult(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Follows <c>next</c> until it is absent; returns every page in order.</summary>
    private async Task<List<PageResult>> ReadAllPagesAsync(string sid, string sql, string resultCache = "bypass", Func<int, Task>? afterPage = null)
    {
        var pages = new List<PageResult>();
        string? next = null;
        do
        {
            var page = await PostAsync(sid, sql, next, resultCache);
            page.Status.ShouldBe(HttpStatusCode.OK, page.Body);
            pages.Add(page);
            if (afterPage != null)
            {
                await afterPage(pages.Count);
            }

            next = page.Next;
            pages.Count.ShouldBeLessThan(50, "paging must terminate");
        }
        while (next != null);

        return pages;
    }

    private static List<string> Ints(IEnumerable<int> values) => values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

    [Fact]
    public async Task ReadsAllRowsBeyondMaxOffset_InOrderNoDuplicates()
    {
        await RegisterItemsAsync();

        var pages = await ReadAllPagesAsync("S-1-5-21-KS-ALL", "SELECT id FROM ks_items ORDER BY id");

        pages.Count.ShouldBe(8);
        pages.SelectMany(p => p.FirstColumn()).ToList().ShouldBe(Ints(Enumerable.Range(1, 75)));
        pages.Last().PagingUnavailable.ShouldBeNull();
        pages.Last().Next.ShouldBeNull();
    }

    [Fact]
    public async Task CompositeKey_MixedDirections_TextAndDateKeys()
    {
        await RegisterAsync("ks_comp", ["region", "day"], [new("region", "varchar"), new("day", "date"), new("tenant_id", "varchar")]);

        var pages = await ReadAllPagesAsync("S-1-5-21-KS-COMP", "SELECT region, day FROM ks_comp ORDER BY region DESC, day ASC");

        var rows = pages.SelectMany(p => p.FirstColumn()).ToList();
        rows.Count.ShouldBe(80);
        rows.Distinct().Count().ShouldBe(4, "the first column repeats per region; the block order is what matters");
        rows.Distinct().ToList().ShouldBe(["r4", "r3", "r2", "r1"]);

        // Day order inside a region: re-read both columns and compare to the generated sequence.
        var all = new List<(string Region, string Day)>();
        foreach (var page in pages)
        {
            using var doc = page.Json;
            var names = doc.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();
            foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                var region = row.ValueKind == JsonValueKind.Array ? row[0] : row.GetProperty(names[0]);
                var day = row.ValueKind == JsonValueKind.Array ? row[1] : row.GetProperty(names[1]);
                all.Add((region.GetString()!, day.GetString()![..10]));
            }
        }

        var expected = Enumerable.Range(1, 4).Reverse()
            .SelectMany(r => Enumerable.Range(0, 20).Select(d => ($"r{r}", new DateTime(2024, 1, 1).AddDays(d).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))))
            .ToList();
        all.ShouldBe(expected);
    }

    [Fact]
    public async Task UuidAndTimestampKeys_PostgreSql()
    {
        await RegisterAsync("ks_uuid", ["id"], [new("id", "uuid"), new("tenant_id", "varchar")]);
        await RegisterAsync("ks_ts", ["ts"], [new("ts", "timestamp"), new("n", "integer"), new("tenant_id", "varchar")]);

        var uuidPages = await ReadAllPagesAsync("S-1-5-21-KS-UUID", "SELECT id FROM ks_uuid ORDER BY id");
        var uuids = uuidPages.SelectMany(p => p.FirstColumn()).ToList();
        uuids.Count.ShouldBe(75);
        uuids.Distinct().Count().ShouldBe(75);
        uuids.ShouldBe(uuids.Order(StringComparer.Ordinal).ToList());

        var tsPages = await ReadAllPagesAsync("S-1-5-21-KS-TS", "SELECT ts, n FROM ks_ts ORDER BY ts DESC");
        var stamps = tsPages.SelectMany(p => p.FirstColumn()).ToList();
        stamps.Count.ShouldBe(75);
        stamps.Distinct().Count().ShouldBe(75);
        stamps.ShouldBe(stamps.OrderByDescending(s => s, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task InsertBetweenPages_NoSkipNoDuplicate()
    {
        await RegisterItemsAsync("ks_live");
        const string sql = "SELECT id FROM ks_live ORDER BY id";

        var inserted = false;
        var pages = await ReadAllPagesAsync("S-1-5-21-KS-LIVE", sql, afterPage: async count =>
        {
            // After the first keyset page (page 4), insert rows before the cursor position: an offset page would shift and repeat rows.
            if (count == 4 && !inserted)
            {
                inserted = true;
                await using var conn = new NpgsqlConnection(_factory.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("INSERT INTO ks_live VALUES (1, 'tenant_ks'), (3, 'tenant_ks'), (5, 'tenant_ks')", conn);
                await cmd.ExecuteNonQueryAsync();
            }
        });

        var ids = pages.SelectMany(p => p.FirstColumn()).Select(int.Parse).ToList();
        ids.Distinct().Count().ShouldBe(ids.Count, "no duplicates");
        ids.ShouldBe(ids.Order().ToList());
        Enumerable.Range(1, 75).Select(g => g * 2).Where(v => v > ids[PageSize * 3 - 1]).ShouldBeSubsetOf(ids);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RowFilterAppliesOnEveryPage()
    {
        await RegisterItemsAsync("ks_mixed");

        var pages = await ReadAllPagesAsync("S-1-5-21-KS-MIXED", "SELECT id FROM ks_mixed ORDER BY id");

        var ids = pages.SelectMany(p => p.FirstColumn()).Select(int.Parse).ToList();
        ids.Count.ShouldBe(60);
        ids.ShouldBe(Enumerable.Range(1, 90).Where(g => g % 3 != 0).ToList());
        pages.Count.ShouldBeGreaterThan(4, "the read crosses the offset limit");
    }

    [Fact]
    public async Task NonKeyOrder_StopsWithOffsetLimit()
    {
        await RegisterItemsAsync();
        const string sql = "SELECT id, name FROM ks_items ORDER BY name";

        string? next = null;
        PageResult last;
        var guard = 0;
        do
        {
            last = await PostAsync("S-1-5-21-KS-NONKEY", sql, next);
            last.Status.ShouldBe(HttpStatusCode.OK, last.Body);
            next = last.Next;
            guard++;
        }
        while (next != null && guard < 20);

        last.Next.ShouldBeNull();
        last.PagingUnavailable.ShouldBe("offset_limit");
        guard.ShouldBe(3, "pages 1 to 3 are offset pages, the continuation past the limit is refused");
    }

    /// <summary>
    /// Keyset_KeyMaskedAtContinuation_Rejected / Keyset_MaskedKeyColumn_NotEligible: a statement ordered by a masked column is refused
    /// outright, so the observable case is a key that becomes masked between pages. The token must not continue and no row is returned.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task MaskedPkAtContinuation_TokenRejected()
    {
        Column[] columns = [new("id", "varchar"), new("tenant_id", "varchar")];
        await RegisterAsync("ks_masked", ["id"], columns);
        const string sql = "SELECT id FROM ks_masked ORDER BY id";

        string? next = null;
        for (var i = 0; i < 3; i++)
        {
            var page = await PostAsync("S-1-5-21-KS-MASK", sql, next);
            page.Status.ShouldBe(HttpStatusCode.OK, page.Body);
            next = page.Next;
        }

        next.ShouldNotBeNull("the third page issues a keyset token");

        await RegisterAsync("ks_masked", ["id"], columns,
            new Dictionary<string, MaskingRule> { ["id"] = new() { RuleType = "REDACT", Replacement = "XXX" } });

        var rejected = await PostAsync("S-1-5-21-KS-MASK", sql, next);

        ((int)rejected.Status).ShouldBeInRange(400, 499, rejected.Body);
        rejected.Body.ShouldNotContain("\"rows\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KeysetPage_OtherUserToken_Rejected()
    {
        await RegisterItemsAsync();
        const string sql = "SELECT id FROM ks_items ORDER BY id";
        string? next = null;
        for (var i = 0; i < 3; i++)
        {
            var page = await PostAsync("S-1-5-21-KS-OWNER", sql, next);
            page.Status.ShouldBe(HttpStatusCode.OK, page.Body);
            next = page.Next;
        }

        next.ShouldNotBeNull("the third page issues a keyset token");

        var stolen = await PostAsync("S-1-5-21-KS-THIEF", sql, next);

        ((int)stolen.Status).ShouldBeInRange(400, 499, stolen.Body);
        stolen.Body.ShouldNotContain("\"rows\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EveryKeysetPageAudited()
    {
        await RegisterItemsAsync();
        const string sid = "S-1-5-21-KS-AUDIT";

        var pages = await ReadAllPagesAsync(sid, "SELECT id FROM ks_items ORDER BY id");
        var keysetPages = pages.Count - 3;

        int audited = 0;
        await WaitHelper.WaitUntil(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
            var entries = (await repo.GetAuditLogEntriesAsync(2000)).Where(e =>
                e.EventType == Autheris.Domain.Audit.AuditEventTypes.WebSqlQuery &&
                e.Decision == "ALLOW" &&
                e.ActorSid.Value == sid).ToList();
            audited = entries.Count(e => e.DetailsJson != null && e.DetailsJson.Contains("\"keyset\":true", StringComparison.Ordinal));
            return audited >= keysetPages && entries.Count >= pages.Count;
        }, TimeSpan.FromSeconds(15), "one audit entry per page, keyset pages marked", TimeSpan.FromMilliseconds(200));

        audited.ShouldBe(keysetPages);
    }

    [Fact]
    public async Task ResultCacheOn_FirstPageCachedContinuationBypassed()
    {
        await RegisterItemsAsync("ks_cache");
        const string sql = "SELECT id FROM ks_cache ORDER BY id";
        const string sid = "S-1-5-21-KS-CACHE";

        // The early pages are offset pages: the same statement and page are served from the cache once it is stored.
        PageResult first = await PostAsync(sid, sql, resultCache: "default");
        first.Status.ShouldBe(HttpStatusCode.OK, first.Body);
        PageResult hit = first;
        await WaitHelper.WaitUntil(async () =>
        {
            hit = await PostAsync(sid, sql, resultCache: "default");
            return hit.ResultCache.Status == "hit";
        }, TimeSpan.FromSeconds(10), "resultCache.status=hit for the repeated first page", TimeSpan.FromMilliseconds(200));
        hit.FirstColumn().ShouldBe(Ints(Enumerable.Range(1, PageSize)));

        // Continuation pages from the keyset token on never touch the cache.
        var pages = await ReadAllPagesAsync(sid, sql, resultCache: "default");
        pages.SelectMany(p => p.FirstColumn()).ToList().ShouldBe(Ints(Enumerable.Range(1, 75)));
        foreach (var keysetPage in pages.Skip(3))
        {
            keysetPage.ResultCache.ShouldBe(("ineligible", "keyset_page"));
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ResultCacheOn_ContinuationBypassed_NoCrossUserLeak()
    {
        await RegisterItemsAsync("ks_cache");
        const string sql = "SELECT id FROM ks_cache ORDER BY id";

        var owner = await ReadAllPagesAsync("S-1-5-21-KS-LEAK-A", sql, resultCache: "default");
        var other = await ReadAllPagesAsync("S-1-5-21-KS-LEAK-B", sql, resultCache: "default");

        foreach (var page in owner.Skip(3).Concat(other.Skip(3)))
        {
            page.ResultCache.Status.ShouldNotBe("hit");
            page.ResultCache.Reason.ShouldBe("keyset_page");
        }

        other.SelectMany(p => p.FirstColumn()).ToList().ShouldBe(Ints(Enumerable.Range(1, 75)));
    }
}
