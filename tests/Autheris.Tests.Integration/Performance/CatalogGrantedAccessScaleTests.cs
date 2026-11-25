namespace Autheris.Tests.Integration.Performance;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Issue #1043: <c>accessStatus=granted</c> on the catalog object list and facets for a caller with many grants. The number of
/// governance statements a request issues must not grow with the number of granted tables times their columns. The test counts
/// the PostgreSQL commands of each request (Npgsql activities) on a PostgreSQL governance database; the default scale runs in CI,
/// <c>AUTHERIS_GRANTED_SCALE_TABLES</c> raises it (3,000 reproduces the reported deployment).
/// </summary>
public sealed class CatalogGrantedAccessScaleTests : IClassFixture<CatalogGrantedAccessScaleTests.HostFixture>
{
    private const string Tenant = "tenant-scale";
    private const string GroupSid = "S-1-5-21-1043-1043-1043-3001";
    private const string UserSid = "S-1-5-21-1043-1043-1043-1001";
    private const int ColumnsPerTable = 22;

    private readonly HostFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CatalogGrantedAccessScaleTests(HostFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static int TableCount =>
        int.TryParse(Environment.GetEnvironmentVariable("AUTHERIS_GRANTED_SCALE_TABLES"), out var n) && n > 0 ? n : 240;

    // Budgets: the cold request (it builds the caller's access view, and the search index for a search) may read a few statements per
    // 1,000 tables; the warm request (issue #1044: cached access view, issue #1047: batched page) costs a constant number of statements,
    // whatever the number of granted tables.
    [Theory]
    [InlineData("/api/v1/catalog/objects?pageSize=50&accessStatus=granted", 200, 60)]
    [InlineData("/api/v1/catalog/objects?pageSize=50&accessStatus=granted&q=t0001&sort=relevance", 200, 70)]
    [InlineData("/api/v1/catalog/objects/facets?accessStatus=granted", 200, 20)]
    public async Task GrantedAccessFilter_StatementCount_DoesNotGrowWithTheGrantedTables(string path, int budget, int warmBudget)
    {
        await _fixture.SeedAsync(TableCount);

        // The page itself (metadata of its 50 items) costs a bounded number of statements on a cold metadata cache; warm it with an
        // unfiltered page first, so the budget measures the access filter whatever order the tests run in.
        await MeasureAsync("/api/v1/catalog/objects?pageSize=50");

        // Cold (first request of a fresh host scope cache) and warm request.
        var cold = await MeasureAsync(path);
        var warm = await MeasureAsync(path);

        cold.Status.ShouldBe(HttpStatusCode.OK, cold.Body);
        warm.Status.ShouldBe(HttpStatusCode.OK, warm.Body);
        using (var doc = JsonDocument.Parse(warm.Body))
        {
            if (path.Contains("facets", StringComparison.Ordinal))
            {
                doc.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(TableCount);
            }
            else
            {
                // Every seeded table is granted, so the filter keeps a full page (or the one search hit).
                doc.RootElement.GetProperty("items").GetArrayLength().ShouldBe(path.Contains("q=", StringComparison.Ordinal) ? 1 : 50, warm.Body[..Math.Min(400, warm.Body.Length)]);
            }
        }

        // Set-based reads bind at most 1,000 ids per statement, so a few statements per 1,000 columns are allowed on top.
        budget += 4 * ((TableCount * ColumnsPerTable + 999) / 1000);
        cold.Statements.ShouldBeLessThan(budget, $"cold request issued {cold.Statements} statements for {TableCount} granted tables");
        warm.Statements.ShouldBeLessThan(warmBudget, $"warm request issued {warm.Statements} statements for {TableCount} granted tables");
    }

    // Issue #1050: one catalog object (found and unknown) must not read the whole catalog per request.
    [Theory]
    [InlineData("/api/v1/catalog/objects/dom_scale.main.t0001", HttpStatusCode.OK)]
    [InlineData("/api/v1/catalog/objects/dom_scale.main.does_not_exist", HttpStatusCode.NotFound)]
    public async Task SingleObject_StatementCount_DoesNotGrowWithTheCatalog(string path, HttpStatusCode expected)
    {
        await _fixture.SeedAsync(TableCount);
        await MeasureAsync(path);

        var warm = await MeasureAsync(path);

        warm.Status.ShouldBe(expected, warm.Body);
        warm.Statements.ShouldBeLessThan(60, $"a single-object request issued {warm.Statements} statements for {TableCount} tables");
    }

    private async Task<Measurement> MeasureAsync(string path)
    {
        using var client = _fixture.Factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", UserSid);
        client.DefaultRequestHeaders.Add("X-Test-Group-Sids", GroupSid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "Autheris.Catalog.Read");

        // Only the statements of this request count: the request carries its own W3C trace, so statements of the host's background
        // services (catalog snapshot poll, webhook dispatcher, usage flush) and of other hosts still alive in the test process, which
        // the process-wide listener also sees, are reported separately and never charged to the budget.
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("traceparent", $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");

        using var counter = new StatementCounter(traceId);
        var sw = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        sw.Stop();

        _output.WriteLine($"{path}: {(int)response.StatusCode} in {sw.ElapsedMilliseconds} ms, {counter.Count} statements ({counter.TotalMilliseconds:F0} ms in the database), {TableCount} granted tables; {counter.OtherCount} statements of other work in the process meanwhile");
        foreach (var (statement, count, ms) in counter.Top(12))
        {
            _output.WriteLine($"  {count,7} x {ms,8:F1} ms  {statement}");
        }

        // Fail closed on the measurement itself: a request whose trace did not reach the server would count nothing.
        counter.Count.ShouldBeGreaterThan(0, $"no statement of {path} carried the request trace");
        return new Measurement(response.StatusCode, body, counter.Count, sw.Elapsed);
    }

    private sealed record Measurement(HttpStatusCode Status, string Body, int Statements, TimeSpan Elapsed);

    /// <summary>Counts the Npgsql command activities of one request trace while it is alive.</summary>
    private sealed class StatementCounter : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentDictionary<string, (int Count, double Milliseconds)> _byStatement = new(StringComparer.Ordinal);
        private int _count;
        private int _otherCount;

        public StatementCounter(ActivityTraceId traceId)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name.StartsWith("Npgsql", StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    var text = activity.GetTagItem("db.query.text") as string ?? activity.GetTagItem("db.statement") as string;
                    if (text is null)
                    {
                        return; // connection open, physical open, copy etc.
                    }

                    if (activity.TraceId != traceId)
                    {
                        Interlocked.Increment(ref _otherCount);
                        return;
                    }

                    Interlocked.Increment(ref _count);
                    var key = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    key = key.Length > 110 ? key[..110] : key;
                    var ms = activity.Duration.TotalMilliseconds;
                    _byStatement.AddOrUpdate(key, (1, ms), (_, c) => (c.Count + 1, c.Milliseconds + ms));
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public int Count => Volatile.Read(ref _count);

        public int OtherCount => Volatile.Read(ref _otherCount);

        public double TotalMilliseconds => _byStatement.Values.Sum(static v => v.Milliseconds);

        public IEnumerable<(string Statement, int Count, double Milliseconds)> Top(int n) =>
            _byStatement.OrderByDescending(static kv => kv.Value.Milliseconds).Take(n).Select(static kv => (kv.Key, kv.Value.Count, kv.Value.Milliseconds));

        public void Dispose() => _listener.Dispose();
    }

    public sealed class HostFixture : IDisposable
    {
        private readonly SemaphoreSlim _seedLock = new(1, 1);
        private int _seeded;

        public WebApplicationFactory<Program> Root { get; } = new();

        public WebApplicationFactory<Program> Factory { get; }

        public HostFixture()
        {
            Factory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
                b.UseSetting("Gateway:Catalog:Discovery:RequestsPerMinute", "100000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("CatalogGrantedScale"));
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));

                // Issue #1044: the catalog change indexer re-indexes every freshly seeded table in the background (one artifact read
                // per table), which inflated the time and statement count of the measured requests at 10,000 tables.
                b.ConfigureTestServices(s =>
                {
                    foreach (var indexer in s.Where(static d => d.ServiceType == typeof(IHostedService) &&
                        d.ImplementationType == typeof(Autheris.Application.Catalog.External.CatalogChangeIndexerHostedService)).ToList())
                    {
                        s.Remove(indexer);
                    }
                });
            });
        }

        public async Task SeedAsync(int tables)
        {
            if (Volatile.Read(ref _seeded) != 0)
            {
                return;
            }

            await _seedLock.WaitAsync();
            try
            {
                if (_seeded != 0)
                {
                    return;
                }

                using var scope = Factory.Services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
                var batch = new List<TableMetadata>(100);
                var identifiers = new List<TableIdentifier>(tables);
                for (var i = 0; i < tables; i++)
                {
                    var id = new TableIdentifier("dom_scale", "main", $"t{i:D4}");
                    identifiers.Add(id);
                    var columns = new List<TableColumn>(ColumnsPerTable);
                    var classifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase);
                    for (var c = 0; c < ColumnsPerTable; c++)
                    {
                        var name = $"c{c:D2}";
                        var columnId = Guid.NewGuid();
                        columns.Add(new TableColumn { Id = columnId, ColumnName = name, DataType = "text" });
                        // Half of the tables carry no non-public classification (the expensive case of the sensitivity lookup);
                        // the other half classify a third of their columns.
                        if (i % 2 == 1 && c % 3 == 0)
                        {
                            classifications[name] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = columnId.ToString(), Code = ClassificationScopes.PiiCode, Source = "test", AssignedBy = "test", AssignedAt = DateTimeOffset.UtcNow }];
                        }
                    }

                    batch.Add(new TableMetadata
                    {
                        Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "PostgreSql", SourceName = id.Domain },
                        Identifier = id,
                        Columns = columns,
                        ColumnClassifications = classifications
                    });
                    if (batch.Count == 100)
                    {
                        await repo.UpsertTableMetadataBatchAsync(batch);
                        batch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    await repo.UpsertTableMetadataBatchAsync(batch);
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var id in identifiers)
                {
                    var meta = await repo.GetTableMetadataAsync(id);
                    await repo.CreateConsentAsync(new Consent
                    {
                        TableId = meta!.Table.Id,
                        TableIdentifier = id,
                        TenantId = new TenantId(Tenant),
                        Effect = ConsentEffect.Allow,
                        GranteeType = GranteeType.Group,
                        GranteeSid = new Sid(GroupSid),
                        ValidFrom = now.AddDays(-1),
                        ValidTo = now.AddDays(1),
                        ColumnRules = [new ConsentColumnRule { ColumnName = "c00", AccessLevel = ColumnAccessLevel.Clear }]
                    });
                }

                Volatile.Write(ref _seeded, 1);
            }
            finally
            {
                _seedLock.Release();
            }
        }

        public void Dispose()
        {
            Factory.Dispose();
            Root.Dispose();
            _seedLock.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }
}
