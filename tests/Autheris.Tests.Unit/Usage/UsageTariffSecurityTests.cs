namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Usage;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UsageTariffSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);

    [Fact]
    public async Task Tenant_admin_cannot_add_wildcard_tariff()
    {
        var harness = new TariffHarness();
        var ctxSource = harness.CreateHttpContext("TenantAdmin", "tenant-a");
        var resSource = await UsageEndpoints.HandleAddTariffVersionAsync(
            "*",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Tomorrow,
                BasePerQuery: 0.5m,
                UnitLabel: "credits",
                Reason: "attempt wildcard source"),
            ctxSource,
            harness.Service);
        (await ExecuteAsync(resSource, ctxSource)).Status.ShouldBe(StatusCodes.Status403Forbidden);

        var ctxTenant = harness.CreateHttpContext("TenantAdmin", "tenant-a");
        var resTenant = await UsageEndpoints.HandleAddTariffVersionAsync(
            "pg-main",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Tomorrow,
                BasePerQuery: 0.5m,
                UnitLabel: "credits",
                Reason: "attempt wildcard tenant",
                TenantId: "*"),
            ctxTenant,
            harness.Service);
        (await ExecuteAsync(resTenant, ctxTenant)).Status.ShouldBe(StatusCodes.Status403Forbidden);

        harness.Repo.Versions.ShouldBeEmpty();
        harness.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tenant_admin_cannot_add_tariff_for_other_tenant()
    {
        var harness = new TariffHarness();

        var ctxBody = harness.CreateHttpContext("TenantAdmin", "tenant-a");
        var resBody = await UsageEndpoints.HandleAddTariffVersionAsync(
            "pg-main",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Tomorrow,
                BasePerQuery: 0.5m,
                UnitLabel: "credits",
                Reason: "cross-tenant write attempt",
                TenantId: "tenant-b"),
            ctxBody,
            harness.Service);
        (await ExecuteAsync(resBody, ctxBody)).Status.ShouldBe(StatusCodes.Status403Forbidden);

        var ctxQuery = harness.CreateHttpContext("TenantAdmin", "tenant-a");
        var resQuery = await UsageEndpoints.HandleAddTariffVersionAsync(
            "pg-main",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Tomorrow,
                BasePerQuery: 0.5m,
                UnitLabel: "credits",
                Reason: "cross-tenant query attempt"),
            ctxQuery,
            harness.Service,
            tenant: "tenant-b");
        (await ExecuteAsync(resQuery, ctxQuery)).Status.ShouldBe(StatusCodes.Status403Forbidden);

        var ctxRead = harness.CreateHttpContext("TenantAdmin", "tenant-a");
        var resRead = await UsageEndpoints.HandleGetTariffsAsync(
            ctxRead,
            harness.Service,
            tenant: "tenant-b");
        (await ExecuteAsync(resRead, ctxRead)).Status.ShouldBe(StatusCodes.Status403Forbidden);

        harness.Repo.Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Consumer_gets_403_on_tariff_routes()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddGatewayAuthorizationPolicies())
            .BuildServiceProvider();
        var builder = new RecordingRouteBuilder(services);
        UsageEndpoints.MapUsageEndpoints(builder);

        var endpoints = builder.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        AssertPolicy(endpoints, "/api/v1/usage/tariffs", "GET", GatewayPolicies.UsageAdmin);
        AssertPolicy(endpoints, "/api/v1/usage/tariffs/{dataSource}/versions", "GET", GatewayPolicies.UsageAdmin);
        AssertPolicy(endpoints, "/api/v1/usage/tariffs/{dataSource}/versions", "POST", GatewayPolicies.UsageAdmin);

        var authService = services.GetRequiredService<IAuthorizationService>();
        var consumer = TariffHarness.BuildPrincipal("Consumer", "tenant-a", "S-1-5-21-CONSUMER");
        (await authService.AuthorizeAsync(consumer, null, GatewayPolicies.UsageAdmin)).Succeeded.ShouldBeFalse();
        GatewayPolicies.HasAnyRole(consumer, GatewayPolicies.UsageAdminRoles).ShouldBeFalse();
    }

    [Fact]
    public void Put_and_delete_tariff_return_405()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var builder = new RecordingRouteBuilder(services);
        UsageEndpoints.MapUsageEndpoints(builder);

        var endpoints = builder.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        var tariffEndpoints = endpoints
            .Where(e => string.Equals(e.RoutePattern.RawText, "/api/v1/usage/tariffs", StringComparison.Ordinal))
            .ToList();
        tariffEndpoints.SelectMany(e => e.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods))
            .ShouldBe(["GET"], ignoreOrder: true);

        var versionEndpoints = endpoints
            .Where(e => string.Equals(e.RoutePattern.RawText, "/api/v1/usage/tariffs/{dataSource}/versions", StringComparison.Ordinal))
            .ToList();
        versionEndpoints.SelectMany(e => e.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods))
            .ShouldBe(["GET", "POST"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("TenantAdmin", null)]
    [InlineData("ClusterAdmin", "*")]
    public async Task Retroactive_tariff_rejected_for_all_roles(string role, string? targetTenant)
    {
        var harness = new TariffHarness();

        var ctxToday = harness.CreateHttpContext(role, "tenant-a");
        var todayRes = await UsageEndpoints.HandleAddTariffVersionAsync(
            "pg-main",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Today,
                BasePerQuery: 1.0m,
                UnitLabel: "credits",
                Reason: "retroactive today",
                TenantId: targetTenant),
            ctxToday,
            harness.Service);
        (await ExecuteAsync(todayRes, ctxToday)).Status.ShouldBe(StatusCodes.Status400BadRequest);

        var ctxPast = harness.CreateHttpContext(role, "tenant-a");
        var pastRes = await UsageEndpoints.HandleAddTariffVersionAsync(
            "pg-main",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Today.AddDays(-5),
                BasePerQuery: 1.0m,
                UnitLabel: "credits",
                Reason: "retroactive past",
                TenantId: targetTenant),
            ctxPast,
            harness.Service);
        (await ExecuteAsync(pastRes, ctxPast)).Status.ShouldBe(StatusCodes.Status400BadRequest);

        harness.Repo.Versions.ShouldBeEmpty();
        harness.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tariff_change_audited_and_fails_closed_503_without_audit()
    {
        var harness = new TariffHarness();

        // Fail audit store -> 503 USAGE_AUDIT_UNAVAILABLE, no repository write.
        harness.Audit.ShouldFail = true;
        var ctxFail = harness.CreateHttpContext("TenantAdmin", "tenant-a");
        var failRes = await UsageEndpoints.HandleAddTariffVersionAsync(
            "pg-main",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Tomorrow,
                BasePerQuery: 0.5m,
                UnitLabel: "credits",
                Reason: "audit outage test"),
            ctxFail,
            harness.Service);
        var (failStatus, failBody) = await ExecuteAsync(failRes, ctxFail);
        failStatus.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        failBody.ShouldContain(ApiErrorCodes.UsageAuditUnavailable);
        harness.Repo.Versions.ShouldBeEmpty();

        // Restore audit store -> 201 Created, UsageTariffVersionAdded audited before write.
        harness.Audit.ShouldFail = false;
        var ctxOk = harness.CreateHttpContext("TenantAdmin", "tenant-a");
        var okRes = await UsageEndpoints.HandleAddTariffVersionAsync(
            "pg-main",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Tomorrow,
                BasePerQuery: 0.5m,
                UnitLabel: "credits",
                Reason: "approved tariff"),
            ctxOk,
            harness.Service);
        (await ExecuteAsync(okRes, ctxOk)).Status.ShouldBe(StatusCodes.Status201Created);
        harness.Repo.Versions.Count.ShouldBe(1);
        harness.Audit.Entries.Count.ShouldBe(1);
        harness.Audit.Entries[0].EventType.ShouldBe(AuditEventTypes.UsageTariffVersionAdded);
        harness.Audit.Entries[0].DetailsJson.ShouldContain("\"oldVersion\":0");
        harness.Audit.Entries[0].DetailsJson.ShouldContain("\"newVersion\":1");
        harness.Audit.Entries[0].DetailsJson.ShouldContain("approved tariff");
    }

    [Fact]
    public async Task Cluster_admin_can_add_wildcard_tariff()
    {
        var harness = new TariffHarness();
        var ctx = harness.CreateHttpContext("ClusterAdmin", "tenant-a");

        var res = await UsageEndpoints.HandleAddTariffVersionAsync(
            "*",
            new UsageEndpoints.CreateTariffVersionHttpRequest(
                ValidFrom: Tomorrow,
                BasePerQuery: 0.1m,
                UnitLabel: "credits",
                Reason: "cluster default",
                TenantId: "*"),
            ctx,
            harness.Service);

        (await ExecuteAsync(res, ctx)).Status.ShouldBe(StatusCodes.Status201Created);
        harness.Repo.Versions.ShouldContain(v => v.TenantId == "*" && v.DataSource == "*" && v.Version == 1);
    }

    // Helper types placed after all [Fact]/[Theory] methods so SecurityTestInventoryTests records outer class FQN cleanly.
    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext httpContext)
    {
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return (httpContext.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static void AssertPolicy(List<RouteEndpoint> endpoints, string pattern, string method, string expectedPolicy)
    {
        var endpoint = endpoints.FirstOrDefault(e =>
            string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal) &&
            e.Metadata.OfType<HttpMethodMetadata>().Any(m => m.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase)));
        endpoint.ShouldNotBeNull($"Expected {method} {pattern} to be mapped.");
        endpoint!.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy)
            .ShouldContain(expectedPolicy);
        var ingress = endpoint.Metadata.GetMetadata<IngressTransportMetadata>();
        ingress.ShouldNotBeNull();
        ingress!.Transport.ShouldBe(DataSourceTransportFlags.Rest);
    }

    private sealed class TariffHarness
    {
        public InMemorySecurityTariffRepo Repo { get; } = new();
        public ToggleableAuditWriter Audit { get; } = new();
        public FakeTimeProvider Clock { get; } = new(Now);
        public IUsageTariffService Service { get; }
        public IServiceProvider RequestServices { get; }

        public TariffHarness()
        {
            Service = new UsageTariffService(
                Repo,
                Audit,
                Options.Create(new UsageAccountingOptions()),
                NullLogger<UsageTariffService>.Instance,
                Clock,
                ds => ds == "pg-main");

            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<TimeProvider>(Clock)
                .BuildServiceProvider();
        }

        public static ClaimsPrincipal BuildPrincipal(string role, string tenantId, string sid)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.PrimarySid, sid),
                new("sub", sid),
                new("tenant_id", tenantId),
                new(ClaimTypes.Role, role)
            };
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        public DefaultHttpContext CreateHttpContext(string role, string tenantId, string sid = "S-1-5-21-TEST-ADMIN")
        {
            var context = new DefaultHttpContext
            {
                RequestServices = RequestServices,
                User = BuildPrincipal(role, tenantId, sid)
            };
            context.Response.Body = new MemoryStream();
            return context;
        }
    }

    private sealed class RecordingRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotSupportedException();
    }

    private sealed class InMemorySecurityTariffRepo : IUsageRepository
    {
        public List<UsageTariffVersion> Versions { get; } = [];

        public Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default)
        {
            var result = Versions
                .Where(v => (string.Equals(v.TenantId, tenant.Value, StringComparison.OrdinalIgnoreCase) || v.TenantId == "*")
                            && v.ValidFrom <= toUtc
                            && (!v.ValidUntil.HasValue || v.ValidUntil.Value > fromUtc))
                .ToList();
            return Task.FromResult<IReadOnlyList<UsageTariffVersion>>(result);
        }

        public Task<UsageTariffVersion> AddTariffVersionAsync(UsageTariffVersion tariff, CancellationToken ct = default)
        {
            int nextVer = Versions.Count + 1;
            var created = tariff with { Version = nextVer };
            Versions.Add(created);
            return Task.FromResult(created);
        }

        public Task AccumulateAsync(UsageGenerationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default) => Task.FromResult(new UsageReportPage([], null));
        public Task<int> PurgeAsync(DateOnly cutoffExclusive, int batchSize = 10_000, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EraseUserAsync(TenantId tenant, string userKey, CancellationToken ct = default) => Task.FromResult(0);
        public Task<UsageGrantRefRecord?> GetGrantRefAsync(TenantId tenant, string grantId, CancellationToken ct = default) => Task.FromResult<UsageGrantRefRecord?>(null);
        public Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsumerAppDailySummary>>([]);
        public Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task<UsageQuotaTenantSnapshot> GetQuotaSnapshotAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) => Task.FromResult(UsageQuotaTenantSnapshot.Empty);
        public Task<IReadOnlyList<TenantId>> GetActiveUsageTenantsAsync(DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TenantId>>([]);
    }

    private sealed class ToggleableAuditWriter : IAuditWriter
    {
        public bool ShouldFail { get; set; }
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (ShouldFail)
            {
                throw new AuditUnavailableException("Audit pipeline unavailable in test.");
            }

            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
    }
}
