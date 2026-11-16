namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Middleware;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UsageQuotaSecurityTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid UserAlice = new("S-1-5-21-1000-1");
    private static readonly Sid GroupLow = new("S-1-5-21-1000-200");
    private static readonly Sid GroupHigh = new("S-1-5-21-1000-300");

    [Fact]
    public async Task Exceeded_returns_429_code_scope_and_retry_after()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 15, 22, 30, 0, TimeSpan.Zero));
        var options = Options.Create(new UsageAccountingOptions
        {
            Quotas =
            [
                new UsageQuota
                {
                    Scope = UsageQuotaScope.User,
                    Subject = UserAlice.Value,
                    Tenant = TenantA.Value,
                    Period = UsageQuotaPeriod.Day,
                    Metric = UsageQuotaMetric.Requests,
                    Limit = 2m
                }
            ]
        });

        var aggregator = new UsageAggregator(options, timeProvider: clock);
        var repo = new InMemoryQuotaUsageRepo
        {
            Snapshots =
            {
                [TenantA] = new UsageQuotaTenantSnapshot(
                    [
                        new UsageQuotaSeriesEntry(
                            new DateOnly(2026, 6, 15),
                            UserAlice.Value,
                            "none",
                            string.Empty,
                            "pg",
                            Requests: 1,
                            Queries: 1,
                            RowsReturned: 10,
                            Bytes: 100,
                            DurationMs: 5,
                            AnalysisCost: 0,
                            SourceComputeMs: 0,
                            SourceComputeMeasured: 0,
                            SourceCostMicros: 0,
                            AllowCount: 1)
                    ],
                    new Dictionary<string, IReadOnlyList<Sid>>(StringComparer.Ordinal),
                    [])
            }
        };

        var quotaService = new UsageQuotaService(repo, aggregator, options, timeProvider: clock);
        await quotaService.RefreshAsync();

        // One more unflushed request reaches limit (1 + 1 = 2)
        aggregator.Record(
            new UsageIdentity(TenantA, UserAlice, new HashSet<Sid>()),
            DataSourceTransportFlags.Rest,
            caller: null,
            durationMs: 1,
            bytes: 10,
            statusCode: 200,
            facts: []);

        var body = new MemoryStream();
        var ctx = CreateHttpContext("/api/v1/query", TenantA, UserAlice, new HashSet<Sid>(), body);
        bool nextCalled = false;
        var middleware = new UsageMeteringMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, aggregator, quotaService: quotaService);

        nextCalled.ShouldBeFalse();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        ctx.Response.Headers.RetryAfter.ToString().ShouldBe("5400"); // 1.5h until 2026-06-16T00:00:00Z

        body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(body);
        var root = doc.RootElement;
        root.GetProperty("code").GetString().ShouldBe(ApiErrorCodes.UsageQuotaExceeded);
        root.GetProperty("scope").GetString().ShouldBe(UsageQuotaScope.User.ToString());
        root.GetProperty("metric").GetString().ShouldBe(UsageQuotaMetric.Requests.ToString());
        root.GetProperty("period").GetString().ShouldBe(UsageQuotaPeriod.Day.ToString());
        root.GetProperty("retryAfterSeconds").GetInt32().ShouldBe(5400);

        var flushed = aggregator.Swap();
        flushed.Series.Values.Sum(v => v.DenyQuota).ShouldBe(1);
    }

    [Fact]
    public async Task Most_restrictive_group_quota_applies()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new UsageAccountingOptions
        {
            Quotas =
            [
                new UsageQuota
                {
                    Scope = UsageQuotaScope.Group,
                    Subject = GroupHigh.Value,
                    Tenant = TenantA.Value,
                    Period = UsageQuotaPeriod.Month,
                    Metric = UsageQuotaMetric.Requests,
                    Limit = 100m
                },
                new UsageQuota
                {
                    Scope = UsageQuotaScope.Group,
                    Subject = GroupLow.Value,
                    Tenant = TenantA.Value,
                    Period = UsageQuotaPeriod.Month,
                    Metric = UsageQuotaMetric.Requests,
                    Limit = 3m
                }
            ]
        });

        var aggregator = new UsageAggregator(options, timeProvider: clock);
        var repo = new InMemoryQuotaUsageRepo
        {
            Snapshots = { [TenantA] = UsageQuotaTenantSnapshot.Empty }
        };
        var quotaService = new UsageQuotaService(repo, aggregator, options, timeProvider: clock);
        await quotaService.RefreshAsync();

        var userGroups = new HashSet<Sid> { GroupHigh, GroupLow };
        for (int i = 0; i < 3; i++)
        {
            aggregator.Record(
                new UsageIdentity(TenantA, UserAlice, userGroups),
                DataSourceTransportFlags.Rest,
                caller: null,
                durationMs: 1,
                bytes: 10,
                statusCode: 200,
                facts: []);
        }

        var body = new MemoryStream();
        var ctx = CreateHttpContext("/api/v1/query", TenantA, UserAlice, userGroups, body);
        var middleware = new UsageMeteringMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx, aggregator, quotaService: quotaService);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(ApiErrorCodes.UsageQuotaExceeded);
        doc.RootElement.GetProperty("scope").GetString().ShouldBe(UsageQuotaScope.Group.ToString());
        doc.RootElement.GetProperty("limit").GetDecimal().ShouldBe(3m);
    }

    [Fact]
    public async Task Stale_totals_fail_closed_503_for_quota_subjects()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new UsageAccountingOptions
        {
            QuotaStalenessSeconds = 60,
            Quotas =
            [
                new UsageQuota
                {
                    Scope = UsageQuotaScope.Tenant,
                    Subject = TenantA.Value,
                    Tenant = TenantA.Value,
                    Period = UsageQuotaPeriod.Day,
                    Metric = UsageQuotaMetric.Requests,
                    Limit = 100m
                }
            ]
        });

        var aggregator = new UsageAggregator(options, timeProvider: clock);
        var repo = new InMemoryQuotaUsageRepo();
        var quotaService = new UsageQuotaService(repo, aggregator, options, timeProvider: clock);

        await quotaService.RefreshAsync();

        // Advance beyond QuotaStalenessSeconds (60s) and simulate outage on refresh
        clock.Advance(TimeSpan.FromSeconds(61));
        repo.ThrowOnQuery = new InvalidOperationException("DB unreachable");
        await quotaService.RefreshAsync();

        var body = new MemoryStream();
        var ctx = CreateHttpContext("/api/v1/query", TenantA, UserAlice, new HashSet<Sid>(), body);
        bool nextCalled = false;
        var middleware = new UsageMeteringMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, aggregator, quotaService: quotaService);

        nextCalled.ShouldBeFalse();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Headers.RetryAfter.ToString().ShouldBe("30");

        body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(ApiErrorCodes.UsageQuotaUnavailable);
    }

    [Fact]
    public async Task Callers_without_quota_unaffected_by_store_outage()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new UsageAccountingOptions
        {
            QuotaStalenessSeconds = 60,
            Quotas =
            [
                new UsageQuota
                {
                    Scope = UsageQuotaScope.Tenant,
                    Subject = TenantA.Value,
                    Tenant = TenantA.Value,
                    Period = UsageQuotaPeriod.Day,
                    Metric = UsageQuotaMetric.Requests,
                    Limit = 100m
                }
            ]
        });

        var aggregator = new UsageAggregator(options, timeProvider: clock);
        var repo = new InMemoryQuotaUsageRepo { ThrowOnQuery = new InvalidOperationException("DB unreachable") };
        var quotaService = new UsageQuotaService(repo, aggregator, options, timeProvider: clock);
        await quotaService.RefreshAsync();

        var body = new MemoryStream();
        var ctx = CreateHttpContext("/api/v1/query", TenantB, UserAlice, new HashSet<Sid>(), body);
        bool nextCalled = false;
        var middleware = new UsageMeteringMiddleware(c =>
        {
            nextCalled = true;
            c.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, aggregator, quotaService: quotaService);

        nextCalled.ShouldBeTrue();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Usage_me_exempt_from_quota()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new UsageAccountingOptions
        {
            Quotas =
            [
                new UsageQuota
                {
                    Scope = UsageQuotaScope.User,
                    Subject = UserAlice.Value,
                    Tenant = TenantA.Value,
                    Period = UsageQuotaPeriod.Day,
                    Metric = UsageQuotaMetric.Requests,
                    Limit = 1m
                }
            ]
        });

        var aggregator = new UsageAggregator(options, timeProvider: clock);
        var repo = new InMemoryQuotaUsageRepo
        {
            Snapshots = { [TenantA] = UsageQuotaTenantSnapshot.Empty }
        };
        var quotaService = new UsageQuotaService(repo, aggregator, options, timeProvider: clock);
        await quotaService.RefreshAsync();

        aggregator.Record(
            new UsageIdentity(TenantA, UserAlice, new HashSet<Sid>()),
            DataSourceTransportFlags.Rest,
            caller: null,
            durationMs: 1,
            bytes: 10,
            statusCode: 200,
            facts: []);

        var body = new MemoryStream();
        var ctx = CreateHttpContext("/api/v1/usage/me", TenantA, UserAlice, new HashSet<Sid>(), body);
        bool nextCalled = false;
        var middleware = new UsageMeteringMiddleware(c =>
        {
            nextCalled = true;
            c.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, aggregator, quotaService: quotaService);

        nextCalled.ShouldBeTrue();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Soft_limit_emits_warning_header_and_allows_request()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new UsageAccountingOptions
        {
            Quotas =
            [
                new UsageQuota
                {
                    Scope = UsageQuotaScope.BillingGroup,
                    Subject = GroupLow.Value,
                    Tenant = TenantA.Value,
                    Period = UsageQuotaPeriod.Month,
                    Metric = UsageQuotaMetric.CostUnits,
                    Limit = 10m,
                    SoftLimitRatio = 0.8,
                    UnitLabel = "credits"
                }
            ],
            BillingGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [TenantA.Value] = [GroupLow.Value]
            }
        });

        var aggregator = new UsageAggregator(options, timeProvider: clock);
        var repo = new InMemoryQuotaUsageRepo
        {
            Snapshots =
            {
                [TenantA] = new UsageQuotaTenantSnapshot(
                    [
                        new UsageQuotaSeriesEntry(
                            new DateOnly(2026, 6, 10),
                            UserAlice.Value,
                            "gs-1",
                            GroupLow.Value,
                            "pg",
                            Requests: 8,
                            Queries: 8,
                            RowsReturned: 0,
                            Bytes: 0,
                            DurationMs: 0,
                            AnalysisCost: 0,
                            SourceComputeMs: 0,
                            SourceComputeMeasured: 0,
                            SourceCostMicros: 0,
                            AllowCount: 8)
                    ],
                    new Dictionary<string, IReadOnlyList<Sid>>(StringComparer.Ordinal),
                    [
                        new UsageTariffVersion(
                            TenantA.Value,
                            "pg",
                            1,
                            new DateOnly(2026, 6, 1),
                            null,
                            BasePerQuery: 1m,
                            PerRow: 0m,
                            PerMib: 0m,
                            PerComputeSecond: 0m,
                            PerAnalysisCost: 0m,
                            SourceCostFactor: 0m,
                            SourceCostUnit: null,
                            UnitLabel: "credits",
                            ComputeBasis: 0,
                            CreatedBy: "admin",
                            CreatedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                            Reason: "test")
                    ])
            }
        };

        var quotaService = new UsageQuotaService(repo, aggregator, options, timeProvider: clock);
        await quotaService.RefreshAsync();

        var body = new MemoryStream();
        var ctx = CreateHttpContext("/api/v1/query", TenantA, UserAlice, new HashSet<Sid> { GroupLow }, body);
        var middleware = new UsageMeteringMiddleware(c =>
        {
            c.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, aggregator, quotaService: quotaService);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ctx.Response.Headers["X-Usage-Quota-Warning"].ToString().ShouldBe("BillingGroup;CostUnits");
    }

    private static DefaultHttpContext CreateHttpContext(
        string path,
        TenantId tenant,
        Sid userSid,
        IReadOnlySet<Sid> groups,
        Stream responseBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = responseBody;
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = userSid,
            ResolvedTenant = tenant,
            GroupSids = groups,
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true
        };
        return context;
    }

    private sealed class InMemoryQuotaUsageRepo : IUsageRepository
    {
        public Dictionary<TenantId, UsageQuotaTenantSnapshot> Snapshots { get; } = [];
        public Exception? ThrowOnQuery { get; set; }

        public Task<UsageQuotaTenantSnapshot> GetQuotaSnapshotAsync(
            TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default)
        {
            if (ThrowOnQuery is not null) throw ThrowOnQuery;
            return Task.FromResult(Snapshots.TryGetValue(tenant, out var snap) ? snap : UsageQuotaTenantSnapshot.Empty);
        }

        public Task<IReadOnlyList<TenantId>> GetActiveUsageTenantsAsync(
            DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default)
        {
            if (ThrowOnQuery is not null) throw ThrowOnQuery;
            return Task.FromResult<IReadOnlyList<TenantId>>(Snapshots.Keys.ToList());
        }

        public Task AccumulateAsync(UsageGenerationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default) => Task.FromResult(new UsageReportPage([], null));
        public Task<int> PurgeAsync(DateOnly cutoffExclusive, int batchSize = 10_000, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EraseUserAsync(TenantId tenant, string userKey, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UsageTariffVersion>>([]);
        public Task<UsageTariffVersion> AddTariffVersionAsync(UsageTariffVersion tariff, CancellationToken ct = default) => Task.FromResult(tariff);
        public Task<UsageGrantRefRecord?> GetGrantRefAsync(TenantId tenant, string grantId, CancellationToken ct = default) => Task.FromResult<UsageGrantRefRecord?>(null);
        public Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(TableIdentifier t, TenantId tid, DateTimeOffset f, DateTimeOffset to, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsumerAppDailySummary>>([]);
        public Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(TableIdentifier t, TenantId tid, DateTimeOffset f, DateTimeOffset to, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
