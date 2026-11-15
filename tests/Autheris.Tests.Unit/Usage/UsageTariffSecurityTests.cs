namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        await using var host = await TariffTestHost.CreateAsync();
        var client = host.CreateClient("TenantAdmin", "tenant-a");

        // Wildcard data source "*" -> 403
        var resSource = await client.PostAsJsonAsync("/api/v1/usage/tariffs/*/versions", new
        {
            validFrom = Tomorrow.ToString("yyyy-MM-dd"),
            basePerQuery = 0.5m,
            unitLabel = "credits",
            reason = "attempt wildcard source"
        });
        resSource.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Wildcard tenant "*" -> 403
        var resTenant = await client.PostAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions", new
        {
            tenantId = "*",
            validFrom = Tomorrow.ToString("yyyy-MM-dd"),
            basePerQuery = 0.5m,
            unitLabel = "credits",
            reason = "attempt wildcard tenant"
        });
        resTenant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        host.Repo.Versions.ShouldBeEmpty();
        host.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tenant_admin_cannot_add_tariff_for_other_tenant()
    {
        await using var host = await TariffTestHost.CreateAsync();
        var client = host.CreateClient("TenantAdmin", "tenant-a");

        var resBody = await client.PostAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions", new
        {
            tenantId = "tenant-b",
            validFrom = Tomorrow.ToString("yyyy-MM-dd"),
            basePerQuery = 0.5m,
            unitLabel = "credits",
            reason = "cross-tenant write attempt"
        });
        resBody.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var resQuery = await client.PostAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions?tenant=tenant-b", new
        {
            validFrom = Tomorrow.ToString("yyyy-MM-dd"),
            basePerQuery = 0.5m,
            unitLabel = "credits",
            reason = "cross-tenant query attempt"
        });
        resQuery.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var resRead = await client.GetAsync("/api/v1/usage/tariffs?tenant=tenant-b");
        resRead.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        host.Repo.Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Consumer_gets_403_on_tariff_routes()
    {
        await using var host = await TariffTestHost.CreateAsync();
        var client = host.CreateClient("Consumer", "tenant-a");

        (await client.GetAsync("/api/v1/usage/tariffs")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/usage/tariffs/pg-main/versions")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var postRes = await client.PostAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions", new
        {
            validFrom = Tomorrow.ToString("yyyy-MM-dd"),
            basePerQuery = 0.5m,
            unitLabel = "credits",
            reason = "consumer attempt"
        });
        postRes.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Repo.Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Put_and_delete_tariff_return_405()
    {
        await using var host = await TariffTestHost.CreateAsync();
        var client = host.CreateClient("ClusterAdmin", "tenant-a");

        (await client.PutAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions", new { })).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await client.DeleteAsync("/api/v1/usage/tariffs/pg-main/versions")).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await client.PutAsJsonAsync("/api/v1/usage/tariffs", new { })).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await client.DeleteAsync("/api/v1/usage/tariffs")).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Theory]
    [InlineData("TenantAdmin", null)]
    [InlineData("ClusterAdmin", "*")]
    public async Task Retroactive_tariff_rejected_for_all_roles(string role, string? targetTenant)
    {
        await using var host = await TariffTestHost.CreateAsync();
        var client = host.CreateClient(role, "tenant-a");

        var todayRes = await client.PostAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions", new
        {
            tenantId = targetTenant,
            validFrom = Today.ToString("yyyy-MM-dd"),
            basePerQuery = 1.0m,
            unitLabel = "credits",
            reason = "retroactive today"
        });
        todayRes.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var pastRes = await client.PostAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions", new
        {
            tenantId = targetTenant,
            validFrom = Today.AddDays(-5).ToString("yyyy-MM-dd"),
            basePerQuery = 1.0m,
            unitLabel = "credits",
            reason = "retroactive past"
        });
        pastRes.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        host.Repo.Versions.ShouldBeEmpty();
        host.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tariff_change_audited_and_fails_closed_503_without_audit()
    {
        await using var host = await TariffTestHost.CreateAsync();
        var admin = host.CreateClient("TenantAdmin", "tenant-a");

        // Fail audit store -> 503 USAGE_AUDIT_UNAVAILABLE, no repository write.
        host.Audit.ShouldFail = true;
        var failRes = await admin.PostAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions", new
        {
            validFrom = Tomorrow.ToString("yyyy-MM-dd"),
            basePerQuery = 0.5m,
            unitLabel = "credits",
            reason = "audit outage test"
        });
        failRes.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var problemBody = await failRes.Content.ReadAsStringAsync();
        problemBody.ShouldContain(ApiErrorCodes.UsageAuditUnavailable);
        host.Repo.Versions.ShouldBeEmpty();

        // Restore audit store -> 201 Created, UsageTariffVersionAdded audited before write.
        host.Audit.ShouldFail = false;
        var okRes = await admin.PostAsJsonAsync("/api/v1/usage/tariffs/pg-main/versions", new
        {
            validFrom = Tomorrow.ToString("yyyy-MM-dd"),
            basePerQuery = 0.5m,
            unitLabel = "credits",
            reason = "approved tariff"
        });
        okRes.StatusCode.ShouldBe(HttpStatusCode.Created);
        host.Repo.Versions.Count.ShouldBe(1);
        host.Audit.Entries.Count.ShouldBe(1);
        host.Audit.Entries[0].EventType.ShouldBe(AuditEventTypes.UsageTariffVersionAdded);
        host.Audit.Entries[0].DetailsJson.ShouldContain("\"oldVersion\":0");
        host.Audit.Entries[0].DetailsJson.ShouldContain("\"newVersion\":1");
        host.Audit.Entries[0].DetailsJson.ShouldContain("approved tariff");
    }

    [Fact]
    public async Task Cluster_admin_can_add_wildcard_tariff()
    {
        await using var host = await TariffTestHost.CreateAsync();
        var cluster = host.CreateClient("ClusterAdmin", "tenant-a");

        var res = await cluster.PostAsJsonAsync("/api/v1/usage/tariffs/*/versions", new
        {
            tenantId = "*",
            validFrom = Tomorrow.ToString("yyyy-MM-dd"),
            basePerQuery = 0.1m,
            unitLabel = "credits",
            reason = "cluster default"
        });

        res.StatusCode.ShouldBe(HttpStatusCode.Created);
        host.Repo.Versions.ShouldContain(v => v.TenantId == "*" && v.DataSource == "*" && v.Version == 1);
    }

    // Helper types placed after all [Fact]/[Theory] methods so SecurityTestInventoryTests records outer class FQN cleanly.
    private sealed class TariffTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public InMemorySecurityTariffRepo Repo { get; }
        public ToggleableAuditWriter Audit { get; }

        private TariffTestHost(WebApplication app, InMemorySecurityTariffRepo repo, ToggleableAuditWriter audit)
        {
            _app = app;
            Repo = repo;
            Audit = audit;
        }

        public static async Task<TariffTestHost> CreateAsync()
        {
            var repo = new InMemorySecurityTariffRepo();
            var audit = new ToggleableAuditWriter();
            var clock = new FakeTimeProvider(Now);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IUsageRepository>(repo);
            builder.Services.AddSingleton<IAuditWriter>(audit);
            builder.Services.AddSingleton<IUsageTariffService>(new UsageTariffService(
                repo,
                audit,
                Options.Create(new UsageAccountingOptions()),
                NullLogger<UsageTariffService>.Instance,
                clock,
                ds => ds == "pg-main"));
            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options => options.AddGatewayAuthorizationPolicies());

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapUsageEndpoints();
            await app.StartAsync();

            return new TariffTestHost(app, repo, audit);
        }

        public HttpClient CreateClient(string role, string tenantId)
        {
            var client = _app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Test-Role", role);
            client.DefaultRequestHeaders.Add("X-Test-Tenant", tenantId);
            client.DefaultRequestHeaders.Add("X-Test-Sid", "S-1-5-21-TEST-ADMIN");
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
        }
    }

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var roleValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            string role = roleValues.ToString();
            string tenant = Request.Headers.TryGetValue("X-Test-Tenant", out var t) ? t.ToString() : "default";
            string sid = Request.Headers.TryGetValue("X-Test-Sid", out var s) ? s.ToString() : "S-1-5-21-USER";

            var claims = new List<Claim>
            {
                new(ClaimTypes.PrimarySid, sid),
                new("sub", sid),
                new("tenant_id", tenant),
                new(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, "Test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
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
