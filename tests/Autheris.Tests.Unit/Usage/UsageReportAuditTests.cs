namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
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
public sealed class UsageReportAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Sid AdminSid = new("S-1-5-21-100-200-300-1001");
    private static readonly Sid TargetSid = new("S-1-5-21-100-200-300-1002");

    [Fact]
    public async Task Report_read_records_fail_closed_audit_entry_and_sets_no_store()
    {
        var harness = new AuditHarness();
        var ctx = harness.CreateHttpContext("TenantAdmin", "tenant-a", AdminSid.Value);

        var res = await UsageEndpoints.HandleSimpleAdminReportAsync(
            ctx, harness.Service, "users", UsageGroupBy.User, false, null, null, null, null, null, null, CancellationToken.None);
        var (status, _) = await ExecuteAsync(res, ctx);

        status.ShouldBe(StatusCodes.Status200OK);
        ctx.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        harness.Audit.Entries.Count.ShouldBe(1);
        harness.Audit.Entries[0].EventType.ShouldBe(AuditEventTypes.UsageReportRead);
        harness.Audit.Entries[0].TenantId.Value.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task Audit_failure_returns_503_with_no_report_body()
    {
        var harness = new AuditHarness();
        harness.Audit.FailRequired = true;
        var ctx = harness.CreateHttpContext("TenantAdmin", "tenant-a", AdminSid.Value);

        var res = await UsageEndpoints.HandleSimpleAdminReportAsync(
            ctx, harness.Service, "users", UsageGroupBy.User, false, null, null, null, null, null, null, CancellationToken.None);
        var (status, body) = await ExecuteAsync(res, ctx);

        status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        body.ShouldContain(ApiErrorCodes.UsageAuditUnavailable);
        body.ShouldNotContain("\"rows\"");
    }

    [Fact]
    public async Task Me_report_does_not_fail_on_audit_unavailability_or_write_report_read()
    {
        var harness = new AuditHarness();
        harness.Audit.FailRequired = true;
        var ctx = harness.CreateHttpContext("Consumer", "tenant-a", AdminSid.Value);

        var res = await UsageEndpoints.HandleMeReportAsync(
            ctx, harness.Service, null, null, null, null, null, null, null, CancellationToken.None);
        var (status, _) = await ExecuteAsync(res, ctx);

        status.ShouldBe(StatusCodes.Status200OK);
        harness.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_user_writes_fail_closed_audit_before_erasing_and_503_prevents_erasure()
    {
        var harness = new AuditHarness();
        harness.Audit.FailRequired = true;
        var failCtx = harness.CreateHttpContext("TenantAdmin", "tenant-a", AdminSid.Value);

        var failRes = await UsageEndpoints.HandleEraseUserAsync(TargetSid.Value, failCtx, harness.Service, null, CancellationToken.None);
        var (failStatus, _) = await ExecuteAsync(failRes, failCtx);
        failStatus.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        harness.Repo.ErasedKeys.ShouldBeEmpty();

        harness.Audit.FailRequired = false;
        var okCtx = harness.CreateHttpContext("TenantAdmin", "tenant-a", AdminSid.Value);
        var okRes = await UsageEndpoints.HandleEraseUserAsync(TargetSid.Value, okCtx, harness.Service, null, CancellationToken.None);
        var (okStatus, _) = await ExecuteAsync(okRes, okCtx);
        okStatus.ShouldBe(StatusCodes.Status200OK);
        harness.Audit.Entries.Count.ShouldBe(1);
        harness.Audit.Entries[0].EventType.ShouldBe(AuditEventTypes.UsageDataErased);
        harness.Repo.ErasedKeys.ShouldBe([TargetSid.Value]);
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return (ctx.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class AuditHarness
    {
        public RecordingAuditWriter Audit { get; } = new();
        public EraseTrackingUsageRepo Repo { get; } = new();
        public UsageReportService Service { get; }
        private readonly ServiceProvider _sp = new ServiceCollection().AddLogging().BuildServiceProvider();

        public AuditHarness()
        {
            var opts = Options.Create(new UsageAccountingOptions());
            var time = new FakeTimeProvider(Now);
            Service = new UsageReportService(Repo, new UsageGroupNameResolver(opts, timeProvider: time), Audit, opts, time);
        }

        public DefaultHttpContext CreateHttpContext(string role, string tenantId, string sid)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.PrimarySid, sid),
                new("sub", sid),
                new("tenant_id", tenantId),
                new(ClaimTypes.Role, role)
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

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public bool FailRequired { get; set; }
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (FailRequired) throw new AuditUnavailableException("Audit store down.");
            Entries.Add(entry);
            return Task.CompletedTask;
        }
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
    }

    private sealed class EraseTrackingUsageRepo : IUsageRepository
    {
        public List<string> ErasedKeys { get; } = [];
        public Task AccumulateAsync(UsageGenerationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default) =>
            Task.FromResult(new UsageReportPage([], null));
        public Task<int> PurgeAsync(DateOnly cutoffExclusive, int batchSize = 10_000, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EraseUserAsync(TenantId tenant, string userKey, CancellationToken ct = default)
        {
            ErasedKeys.Add(userKey);
            return Task.FromResult(3);
        }
        public Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UsageTariffVersion>>([]);
        public Task<UsageTariffVersion> AddTariffVersionAsync(UsageTariffVersion tariff, CancellationToken ct = default) => Task.FromResult(tariff);
        public Task<UsageGrantRefRecord?> GetGrantRefAsync(TenantId tenant, string grantId, CancellationToken ct = default) => Task.FromResult<UsageGrantRefRecord?>(null);
        public Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(TableIdentifier t, TenantId tid, DateTimeOffset f, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConsumerAppDailySummary>>([]);
        public Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(TableIdentifier t, TenantId tid, DateTimeOffset f, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    }
}
