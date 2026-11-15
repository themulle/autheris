namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Databricks;
using Autheris.Infrastructure.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SourceCostCollectorSecurityTests
{
    private const string Host = "adb-123.azuredatabricks.net";
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly TenantId TenantMissing = new("tenant-missing");
    private static readonly Sid UserSid = new("S-1-5-21-100-200-300");
    private static readonly TableIdentifier TableId = new("sales", "dbo", "orders");

    [Fact]
    public async Task Collector_uses_tenant_principal_only()
    {
        var seenAuthHeaders = new List<(string Query, string? Auth)>();
        var handler = new FakeHttpHandler(req =>
        {
            seenAuthHeaders.Add((req.RequestUri!.Query, req.Headers.Authorization?.ToString()));
            return JsonResponse(HttpStatusCode.OK, "{\"res\":[]}");
        });

        using var http = new HttpClient(handler);
        var creds = new TenantIsolatedCredentialProvider();
        var options = CreateMultiTenantOptions(fallbackToGlobal: true, mode: DatabricksAuthMode.OAuthM2M);
        var collector = new DatabricksSourceCostCollector(http, options, creds);

        // Batch mixes tenant-a and tenant-b: collector must split and acquire each tenant's own principal.
        await collector.CollectAsync(
            [MakePending(TenantA, "stmt-a"), MakePending(TenantB, "stmt-b")],
            CancellationToken.None);

        seenAuthHeaders.Count.ShouldBe(2);
        seenAuthHeaders.ShouldContain(h => h.Query.Contains("stmt-a", StringComparison.Ordinal) && h.Auth == "Bearer tok-tenant-a");
        seenAuthHeaders.ShouldContain(h => h.Query.Contains("stmt-b", StringComparison.Ordinal) && h.Auth == "Bearer tok-tenant-b");

        // Missing tenant must fail closed even when FallbackToGlobalPrincipal is true.
        await Should.ThrowAsync<DatabricksProviderException>(() =>
            collector.CollectAsync([MakePending(TenantMissing, "stmt-missing")], CancellationToken.None));

        // OAuthObo mode must also fail closed when no user caller token exists (no fallback to service principal).
        var oboCollector = new DatabricksSourceCostCollector(
            http,
            CreateMultiTenantOptions(fallbackToGlobal: false, mode: DatabricksAuthMode.OAuthObo),
            new DatabricksOboTokenProvider(http));
        await Should.ThrowAsync<DatabricksProviderException>(() =>
            oboCollector.CollectAsync([MakePending(TenantA, "stmt-obo")], CancellationToken.None));
    }

    [Fact]
    public async Task Collector_response_values_bounded()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new UsageAggregator(timeProvider: time);

        long malformedDelta = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, ml) =>
        {
            if (inst.Name == "autheris_usage_source_cost_malformed_total") ml.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref malformedDelta, val));
        listener.Start();

        // First test DatabricksSourceCostCollector JSON parsing bounds.
        var handler = new FakeHttpHandler(_ => JsonResponse(HttpStatusCode.OK, """
        {
          "res": [
            { "statement_id": "stmt-neg", "metrics": { "total_time_ms": -5 } },
            { "statement_id": "stmt-huge", "metrics": { "total_time_ms": 1000000000001 } },
            { "statement_id": "stmt-badcost", "metrics": { "total_time_ms": 100, "cost": -0.5 } },
            { "statement_id": "stmt-nan", "metrics": { "total_time_ms": "NaN" } },
            { "statement_id": "stmt-ok", "metrics": { "total_time_ms": 1000000000000, "cost": 12.5 } }
          ]
        }
        """));

        using var http = new HttpClient(handler);
        var dbxCollector = new DatabricksSourceCostCollector(
            http,
            CreateMultiTenantOptions(fallbackToGlobal: false, mode: DatabricksAuthMode.OAuthM2M),
            new TenantIsolatedCredentialProvider());

        var parsed = await dbxCollector.CollectAsync(
            [
                MakePending(TenantA, "stmt-neg"),
                MakePending(TenantA, "stmt-huge"),
                MakePending(TenantA, "stmt-badcost"),
                MakePending(TenantA, "stmt-nan"),
                MakePending(TenantA, "stmt-ok")
            ],
            CancellationToken.None);

        parsed.ShouldHaveSingleItem().SourceQueryId.ShouldBe("stmt-ok");
        parsed[0].SourceComputeMs.ShouldBe(1_000_000_000_000L);
        parsed[0].SourceCost.ShouldBe(12.5m);
        malformedDelta.ShouldBeGreaterThanOrEqualTo(4);

        // Next verify SourceCostCollectorWorker itself enforces bounds regardless of collector implementation.
        var rogueCollector = new FixedCollector(
            [
                new SourceCostSample("stmt-w-neg", -1, 1m, "DBU"),
                new SourceCostSample("stmt-w-over", 1_000_000_000_001L, 1m, "DBU"),
                new SourceCostSample("stmt-w-costover", 10, 1_000_000_000_001m, "DBU")
            ]);
        using var worker = new SourceCostCollectorWorker(
            aggregator,
            [rogueCollector],
            CreateMultiTenantOptions(fallbackToGlobal: false, mode: DatabricksAuthMode.OAuthM2M),
            new CapturingLogger<SourceCostCollectorWorker>(),
            time);

        RecordFact(aggregator, "stmt-w-neg");
        RecordFact(aggregator, "stmt-w-over");
        RecordFact(aggregator, "stmt-w-costover");
        _ = aggregator.Swap();

        await worker.CollectOnceAsync(CancellationToken.None);
        var postSnap = aggregator.Swap();
        postSnap.Series.ShouldBeEmpty();
    }

    [Fact]
    public async Task Collector_failure_never_affects_requests()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new UsageAggregator(timeProvider: time, maxPendingQueries: 1);
        var failingCollector = new ThrowingCollector(new HttpRequestException("upstream down"));
        var logger = new CapturingLogger<SourceCostCollectorWorker>();
        using var worker = new SourceCostCollectorWorker(
            aggregator,
            [failingCollector],
            CreateMultiTenantOptions(fallbackToGlobal: false, mode: DatabricksAuthMode.OAuthM2M),
            logger,
            time);

        // Request-path recording succeeds synchronously even when queue hits cap.
        RecordFact(aggregator, "stmt-req-1");
        RecordFact(aggregator, "stmt-req-2");

        // Worker collection fails softly without throwing into caller/host.
        await Should.NotThrowAsync(() => worker.CollectOnceAsync(CancellationToken.None));

        var snap = aggregator.Swap();
        long totalRequests = 0;
        foreach (var counter in snap.Series.Values)
        {
            totalRequests += counter.Requests;
        }

        totalRequests.ShouldBe(2);
    }

    [Fact]
    public async Task Source_query_ids_not_in_logs_or_metrics()
    {
        const string secretQueryId = "secret-stmt-01ef9999-leak-probe";
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new UsageAggregator(timeProvider: time);
        var workerLogger = new CapturingLogger<SourceCostCollectorWorker>();
        var collectorLogger = new CapturingLogger<DatabricksSourceCostCollector>();

        var emittedMetricInfo = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, ml) =>
        {
            if (inst.Name.StartsWith("autheris_usage_", StringComparison.Ordinal))
            {
                emittedMetricInfo.Add($"{inst.Name}:{inst.Description}");
                ml.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, _, tags, _) =>
        {
            emittedMetricInfo.Add(inst.Name);
            foreach (var tag in tags)
            {
                emittedMetricInfo.Add($"{tag.Key}={tag.Value}");
            }
        });
        listener.Start();

        // Trigger Databricks collector HTTP warning, malformed metric, worker batch error, and expiry.
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler);
        var dbxCollector = new DatabricksSourceCostCollector(
            http,
            CreateMultiTenantOptions(fallbackToGlobal: false, mode: DatabricksAuthMode.OAuthM2M),
            new TenantIsolatedCredentialProvider(),
            collectorLogger);

        using var worker = new SourceCostCollectorWorker(
            aggregator,
            [dbxCollector],
            CreateMultiTenantOptions(fallbackToGlobal: false, mode: DatabricksAuthMode.OAuthM2M),
            workerLogger,
            time);

        RecordFact(aggregator, secretQueryId);
        await worker.CollectOnceAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromDays(8));
        await worker.CollectOnceAsync(CancellationToken.None);

        foreach (string msg in workerLogger.Messages)
        {
            msg.ShouldNotContain(secretQueryId, Case.Insensitive);
        }

        foreach (string msg in collectorLogger.Messages)
        {
            msg.ShouldNotContain(secretQueryId, Case.Insensitive);
        }

        foreach (string metric in emittedMetricInfo)
        {
            metric.ShouldNotContain(secretQueryId, Case.Insensitive);
        }
    }

    private static PendingSourceQuery MakePending(TenantId tenant, string queryId) =>
        new(
            tenant,
            "dbx-main",
            queryId,
            new DateOnly(2026, 11, 15),
            new byte[32],
            new UsageSeriesKey(tenant, new DateOnly(2026, 11, 15), "u1", "gs1", "default", DataSourceTransportFlags.Rest, "app", "", "dbx-main", TableId.ToString()),
            Array.Empty<string>(),
            new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));

    private static void RecordFact(UsageAggregator aggregator, string statementId)
    {
        aggregator.Record(
            new UsageIdentity(TenantA, UserSid, new HashSet<Sid>()),
            DataSourceTransportFlags.Rest,
            null,
            10,
            64,
            200,
            [new UsageFact(
                TableId,
                "dbx-main",
                UsageOutcome.Allow,
                1,
                true,
                0,
                Array.Empty<GrantRef>(),
                new SourceExecutionStats(null, null, null, statementId))]);
    }

    private static IOptions<GatewayOptions> CreateMultiTenantOptions(bool fallbackToGlobal, DatabricksAuthMode mode) =>
        Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dbx-main"] = new()
                    {
                        Provider = "Databricks",
                        Databricks = new DatabricksDataSourceOptions
                        {
                            Host = Host,
                            WarehouseId = "wh-1",
                            Auth = new DatabricksAuthOptions
                            {
                                Mode = mode,
                                ClientId = "global-client",
                                ClientSecretRef = "env:GLOBAL_SECRET",
                                FallbackToGlobalPrincipal = fallbackToGlobal,
                                Obo = mode == DatabricksAuthMode.OAuthObo
                                    ? new DatabricksOboOptions { ClientId = "obo-client", ClientSecretRef = "env:OBO" }
                                    : null,
                                Tenants = new Dictionary<string, DatabricksTenantPrincipalOptions>(StringComparer.OrdinalIgnoreCase)
                                {
                                    [TenantA.Value] = new() { ClientId = "client-a", ClientSecretRef = "env:SEC_A" },
                                    [TenantB.Value] = new() { ClientId = "client-b", ClientSecretRef = "env:SEC_B" }
                                }
                            }
                        }
                    }
                }
            }
        });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Helper types placed after all [Fact]/[Theory] methods so SecurityTestInventoryTests records outer class FQN cleanly.
    private sealed class TenantIsolatedCredentialProvider : IDatabricksCredentialProvider
    {
        public Task<string> AcquireTokenAsync(DatabricksDataSourceOptions db, DelegationContext? context, CancellationToken ct)
        {
            if (context?.TenantId is not { } tenant || !db.Auth.Tenants.ContainsKey(tenant.Value))
            {
                throw new DatabricksProviderException(DatabricksErrorCodes.Configuration, "Missing tenant principal.");
            }

            return Task.FromResult($"tok-{tenant.Value}");
        }

        public void EvictToken(DatabricksDataSourceOptions db, DelegationContext? context)
        {
        }

        public void InvalidateSecret(DatabricksDataSourceOptions db, string? tenantId = null)
        {
        }
    }

    private sealed class FixedCollector(IReadOnlyList<SourceCostSample> samples) : ISourceCostCollector
    {
        public string SourceType => "Databricks";
        public string CostUnit => "DBU";
        public Task<IReadOnlyList<SourceCostSample>> CollectAsync(IReadOnlyList<PendingSourceQuery> pending, CancellationToken ct) =>
            Task.FromResult(samples);
    }

    private sealed class ThrowingCollector(Exception error) : ISourceCostCollector
    {
        public string SourceType => "Databricks";
        public string CostUnit => "DBU";
        public Task<IReadOnlyList<SourceCostSample>> CollectAsync(IReadOnlyList<PendingSourceQuery> pending, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<SourceCostSample>>(error);
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
