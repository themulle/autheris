namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Application.Audit;
using Autheris.Application.Usage;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UsageVolumeSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly Sid AdminSid = new("S-1-5-21-100-200-300-1001");
    private const string Group1 = "S-1-5-21-100-200-300-2001";

    [Fact]
    public async Task Inverted_or_oversized_range_returns_400_usage_report_invalid_range()
    {
        var harness = new VolumeHarness(maxRangeDays: 30);

        var invCtx = harness.CreateHttpContext();
        var (invStatus, invBody) = await ExecuteAsync(
            await UsageEndpoints.HandleSimpleAdminReportAsync(invCtx, harness.Service, "users", UsageGroupBy.User, false, "2026-11-10", "2026-11-01", null, null, null, null, CancellationToken.None),
            invCtx);
        invStatus.ShouldBe(StatusCodes.Status400BadRequest);
        invBody.ShouldContain(ApiErrorCodes.UsageReportInvalidRange);

        var overCtx = harness.CreateHttpContext();
        var (overStatus, overBody) = await ExecuteAsync(
            await UsageEndpoints.HandleSimpleAdminReportAsync(overCtx, harness.Service, "users", UsageGroupBy.User, false, "2026-10-01", "2026-11-15", null, null, null, null, CancellationToken.None),
            overCtx);
        overStatus.ShouldBe(StatusCodes.Status400BadRequest);
        overBody.ShouldContain(ApiErrorCodes.UsageReportInvalidRange);
    }

    [Fact]
    public async Task Limit_is_clamped_to_max_report_rows_and_sets_truncated()
    {
        var harness = new VolumeHarness(maxReportRows: 2);
        harness.Repo.UsersRows.Add(RowForUser("u1", "pg-main", 1));
        harness.Repo.UsersRows.Add(RowForUser("u2", "pg-main", 1));
        harness.Repo.UsersRows.Add(RowForUser("u3", "pg-main", 1));

        var ctx = harness.CreateHttpContext();
        var (status, body) = await ExecuteAsync(
            await UsageEndpoints.HandleSimpleAdminReportAsync(ctx, harness.Service, "users", UsageGroupBy.User, false, null, null, null, 100, null, null, CancellationToken.None),
            ctx);
        status.ShouldBe(StatusCodes.Status200OK);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("rows").GetArrayLength().ShouldBe(2);
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("nextCursor").GetString().ShouldBe("u2");
    }

    [Fact]
    public async Task Group_report_includes_unmultiplied_tenant_total_and_multi_counted_note()
    {
        var harness = new VolumeHarness();
        harness.Repo.GroupRows.Add(new UsageReportRow(
            Today, null, Group1, null, "pg-main", null, null, null, null, null,
            5, 5, 10, 5, 0, 100, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0));
        harness.Repo.DataSourceRows.Add(new UsageReportRow(
            Today, null, null, null, "pg-main", null, null, null, null, null,
            3, 3, 10, 3, 0, 100, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0));
        harness.Repo.Tariffs.Add(new UsageTariffVersion("tenant-a", "pg-main", 1, Today.AddDays(-10), null, 2m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "seed", Now, "seed"));

        var ctx = harness.CreateHttpContext();
        var (status, body) = await ExecuteAsync(
            await UsageEndpoints.HandleSimpleAdminReportAsync(ctx, harness.Service, "groups", UsageGroupBy.Group, true, null, null, null, null, null, null, CancellationToken.None),
            ctx);
        status.ShouldBe(StatusCodes.Status200OK);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("multiCounted").GetBoolean().ShouldBeTrue();
        (doc.RootElement.GetProperty("multiCountedNote").GetString() ?? string.Empty).ShouldContain("multi-counted");
        doc.RootElement.GetProperty("tenantTotal").GetProperty("queries").GetInt64().ShouldBe(3);
        doc.RootElement.GetProperty("tenantTotal").GetProperty("cost")[0].GetProperty("costUnits").GetDecimal().ShouldBe(6m);
        doc.RootElement.GetProperty("rows")[0].GetProperty("queries").GetInt64().ShouldBe(5);
    }

    [Fact]
    public async Task Multi_data_source_rollup_omits_source_cost_and_unit_while_keeping_separate_cost_buckets()
    {
        var harness = new VolumeHarness();
        harness.Repo.UsersRows.Add(RowForUser("u1", "pg-main", 1, sourceCostMicros: 1_500_000));
        harness.Repo.UsersRows.Add(RowForUser("u1", "dbx-main", 1, sourceCostMicros: 2_500_000));
        harness.Repo.Tariffs.Add(new UsageTariffVersion("tenant-a", "pg-main", 1, Today.AddDays(-10), null, 1m, 0m, 0m, 0m, 0m, 0m, "USD", "credits", 0, "seed", Now, "seed"));
        harness.Repo.Tariffs.Add(new UsageTariffVersion("tenant-a", "dbx-main", 1, Today.AddDays(-10), null, 2m, 0m, 0m, 0m, 0m, 0m, "DBU", "eur", 0, "seed", Now, "seed"));

        var ctx = harness.CreateHttpContext();
        var (status, body) = await ExecuteAsync(
            await UsageEndpoints.HandleSimpleAdminReportAsync(ctx, harness.Service, "users", UsageGroupBy.User, false, null, null, null, null, null, null, CancellationToken.None),
            ctx);
        status.ShouldBe(StatusCodes.Status200OK);

        using var doc = JsonDocument.Parse(body);
        var row = doc.RootElement.GetProperty("rows")[0];
        row.TryGetProperty("sourceCost", out _).ShouldBeFalse();
        row.TryGetProperty("sourceCostUnit", out _).ShouldBeFalse();
        row.GetProperty("cost").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Deny_only_rows_cost_zero_without_marking_tariff_missing()
    {
        var harness = new VolumeHarness();
        harness.Repo.UsersRows.Add(new UsageReportRow(
            Today, "u1", null, null, "unconfigured-ds", null, null, null, null, null,
            4, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0));

        var ctx = harness.CreateHttpContext();
        var (status, body) = await ExecuteAsync(
            await UsageEndpoints.HandleSimpleAdminReportAsync(ctx, harness.Service, "users", UsageGroupBy.User, false, null, null, null, null, null, null, CancellationToken.None),
            ctx);
        status.ShouldBe(StatusCodes.Status200OK);

        using var doc = JsonDocument.Parse(body);
        var row = doc.RootElement.GetProperty("rows")[0];
        row.GetProperty("tariffMissing").GetBoolean().ShouldBeFalse();
        row.GetProperty("cost").GetArrayLength().ShouldBe(0);
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return (ctx.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static UsageReportRow RowForUser(string userKey, string ds, long allowCount, long sourceCostMicros = 0) =>
        new(Today, userKey, null, null, ds, null, null, null, null, null,
            allowCount, allowCount, 0, allowCount, 0, 0, 0, 0, 0, sourceCostMicros, allowCount,
            allowCount, 0, 0, 0, 0, 0, 0);

    private sealed class VolumeHarness
    {
        public BreakdownUsageRepo Repo { get; } = new();
        public UsageReportService Service { get; }
        private readonly ServiceProvider _sp = new ServiceCollection().AddLogging().BuildServiceProvider();

        public VolumeHarness(int maxRangeDays = 30, int maxReportRows = 500)
        {
            var opts = Options.Create(new UsageAccountingOptions
            {
                MaxReportRangeDays = maxRangeDays,
                MaxReportRows = maxReportRows
            });
            var time = new FakeTimeProvider(Now);
            Service = new UsageReportService(Repo, new UsageGroupNameResolver(opts, timeProvider: time), new NoOpAuditWriter(), opts, time);
        }

        public DefaultHttpContext CreateHttpContext()
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.PrimarySid, AdminSid.Value),
                new("sub", AdminSid.Value),
                new("tenant_id", "tenant-a"),
                new(ClaimTypes.Role, "TenantAdmin")
            };
            var ctx = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
                RequestServices = _sp
            };
            ctx.Response.Body = new MemoryStream();
            return ctx;
        }
    }

    private sealed class NoOpAuditWriter : IAuditWriter
    {
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BreakdownUsageRepo : IUsageRepository
    {
        public List<UsageReportRow> UsersRows { get; } = [];
        public List<UsageReportRow> GroupRows { get; } = [];
        public List<UsageReportRow> DataSourceRows { get; } = [];
        public List<UsageTariffVersion> Tariffs { get; } = [];

        public Task AccumulateAsync(UsageGenerationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PurgeAsync(DateOnly cutoffExclusive, int batchSize = 10_000, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EraseUserAsync(TenantId tenant, string userKey, CancellationToken ct = default) => Task.FromResult(0);
        public Task<UsageTariffVersion> AddTariffVersionAsync(UsageTariffVersion tariff, CancellationToken ct = default) => Task.FromResult(tariff);
        public Task<UsageGrantRefRecord?> GetGrantRefAsync(TenantId tenant, string grantId, CancellationToken ct = default) => Task.FromResult<UsageGrantRefRecord?>(null);
        public Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(TableIdentifier t, TenantId tid, DateTimeOffset f, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConsumerAppDailySummary>>([]);
        public Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(TableIdentifier t, TenantId tid, DateTimeOffset f, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UsageTariffVersion>>(Tariffs);

        public Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default)
        {
            var source = query.GroupBy switch
            {
                UsageGroupBy.Group => GroupRows,
                UsageGroupBy.DataSource => DataSourceRows,
                _ => UsersRows
            };

            var dims = new List<string>();
            var pageRows = new List<UsageReportRow>();
            string? nextCursor = null;

            foreach (var r in source)
            {
                string key = query.GroupBy == UsageGroupBy.User ? (r.UserKey ?? string.Empty)
                    : query.GroupBy == UsageGroupBy.Group ? (r.GroupSid ?? string.Empty)
                    : (r.DataSource ?? string.Empty);
                if (!dims.Contains(key))
                {
                    if (dims.Count >= query.Limit)
                    {
                        nextCursor = dims[^1];
                        break;
                    }
                    dims.Add(key);
                }

                pageRows.Add(r);
            }

            return Task.FromResult(new UsageReportPage(pageRows, nextCursor));
        }

        public Task<UsageQuotaTenantSnapshot> GetQuotaSnapshotAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
            Task.FromResult(UsageQuotaTenantSnapshot.Empty);
        public Task<IReadOnlyList<TenantId>> GetActiveUsageTenantsAsync(DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantId>>([]);
    }
}
