namespace Autheris.Tests.Unit.Usage;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Usage;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UsageReportAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly Sid AliceSid = new("S-1-5-21-100-200-300-1001");
    private static readonly Sid BobSid = new("S-1-5-21-100-200-300-1002");
    private static readonly Sid OwnerSid = new("S-1-5-21-100-200-300-1003");
    private static readonly Sid GroupSid = new("S-1-5-21-100-200-300-2001");

    [Fact]
    public void Mapped_routes_declare_expected_authorization_policies_and_rest_ingress()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var builder = new RecordingRouteBuilder(services);
        UsageEndpoints.MapUsageEndpoints(builder);

        var endpoints = builder.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        AssertPolicy(endpoints, "/api/v1/usage/me", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy(endpoints, "/api/v1/usage/users", GatewayPolicies.UsageAdmin);
        AssertPolicy(endpoints, "/api/v1/usage/groups", GatewayPolicies.UsageAdmin);
        AssertPolicy(endpoints, "/api/v1/usage/tenant", GatewayPolicies.UsageAdmin);
        AssertPolicy(endpoints, "/api/v1/usage/tables/{tableId}", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy(endpoints, "/api/v1/usage/sources", GatewayPolicies.UsageAdmin);
        AssertPolicy(endpoints, "/api/v1/usage/applications", GatewayPolicies.UsageAdmin);
        AssertPolicy(endpoints, "/api/v1/usage/grants/{grantId}", GatewayPolicies.AuthenticatedCaller);
        AssertPolicy(endpoints, "/api/v1/usage/users/{userSid}", GatewayPolicies.UsageAdmin, "DELETE");
    }

    [Fact]
    public async Task Me_returns_only_caller_usage_for_any_authenticated_user()
    {
        var harness = new ReportHarness();
        harness.Repo.Rows.Add(MakeRow(Today, "pg-main", userKey: AliceSid.Value, allowCount: 3, queries: 3));
        harness.Repo.Rows.Add(MakeRow(Today, "pg-main", userKey: BobSid.Value, allowCount: 9, queries: 9));
        harness.Repo.Tariffs.Add(MakeTariff("tenant-a", "pg-main"));

        var ctx = harness.CreateHttpContext("Consumer", "tenant-a", AliceSid.Value);
        var res = await UsageEndpoints.HandleMeReportAsync(ctx, harness.Service, null, null, null, null, null, null, null, CancellationToken.None);
        var (status, body) = await ExecuteAsync(res, ctx);

        status.ShouldBe(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(body);
        var rows = doc.RootElement.GetProperty("rows");
        rows.GetArrayLength().ShouldBe(1);
        rows[0].GetProperty("queries").GetInt64().ShouldBe(3);

        var foreignCtx = harness.CreateHttpContext("Consumer", "tenant-a", AliceSid.Value);
        var foreignRes = await UsageEndpoints.HandleMeReportAsync(foreignCtx, harness.Service, null, null, null, "tenant-b", null, null, null, CancellationToken.None);
        (await ExecuteAsync(foreignRes, foreignCtx)).Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void Usage_admin_policy_forbids_consumer_and_governance_admin()
    {
        var consumer = BuildPrincipal("Consumer", "tenant-a", AliceSid.Value);
        var govAdmin = BuildPrincipal("GovernanceAdmin", "tenant-a", AliceSid.Value);
        var tenantAdmin = BuildPrincipal("TenantAdmin", "tenant-a", AliceSid.Value);
        var clusterAdmin = BuildPrincipal("ClusterAdmin", "platform", AliceSid.Value);

        GatewayPolicies.HasAnyRole(consumer, GatewayPolicies.UsageAdminRoles).ShouldBeFalse();
        GatewayPolicies.HasAnyRole(govAdmin, GatewayPolicies.UsageAdminRoles).ShouldBeFalse();
        GatewayPolicies.HasAnyRole(tenantAdmin, GatewayPolicies.UsageAdminRoles).ShouldBeTrue();
        GatewayPolicies.HasRole(clusterAdmin, Autheris.Domain.Security.GatewayRole.TenantAdmin).ShouldBeTrue();
        Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(clusterAdmin).ShouldBeTrue();
    }

    [Fact]
    public async Task Cross_tenant_query_forbidden_for_non_cluster_admin()
    {
        var harness = new ReportHarness();

        var c1 = harness.CreateHttpContext("TenantAdmin", "tenant-a", AliceSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleSimpleAdminReportAsync(c1, harness.Service, "users", UsageGroupBy.User, false, null, null, "tenant-b", null, null, null, CancellationToken.None), c1))
            .Status.ShouldBe(StatusCodes.Status403Forbidden);

        var c2 = harness.CreateHttpContext("TenantAdmin", "tenant-a", AliceSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleEraseUserAsync(BobSid.Value, c2, harness.Service, "tenant-b", CancellationToken.None), c2))
            .Status.ShouldBe(StatusCodes.Status403Forbidden);

        var c3 = harness.CreateHttpContext("Consumer", "tenant-a", OwnerSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleTableReportAsync("default.public.orders", c3, harness.Service, harness.Options, null, null, null, "tenant-b", null, null, null, CancellationToken.None), c3))
            .Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Cluster_admin_requires_explicit_tenant_and_can_read_any_tenant()
    {
        var harness = new ReportHarness();
        harness.Repo.Rows.Add(MakeRow(Today, "pg-main", tenant: "tenant-b", userKey: BobSid.Value, allowCount: 4, queries: 4));

        var missingUsersCtx = harness.CreateHttpContext("ClusterAdmin", "platform", AliceSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleSimpleAdminReportAsync(missingUsersCtx, harness.Service, "users", UsageGroupBy.User, false, null, null, null, null, null, null, CancellationToken.None), missingUsersCtx))
            .Status.ShouldBe(StatusCodes.Status400BadRequest);

        var missingEraseCtx = harness.CreateHttpContext("ClusterAdmin", "platform", AliceSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleEraseUserAsync(BobSid.Value, missingEraseCtx, harness.Service, null, CancellationToken.None), missingEraseCtx))
            .Status.ShouldBe(StatusCodes.Status400BadRequest);

        var okCtx = harness.CreateHttpContext("ClusterAdmin", "platform", AliceSid.Value);
        var (status, body) = await ExecuteAsync(await UsageEndpoints.HandleSimpleAdminReportAsync(okCtx, harness.Service, "users", UsageGroupBy.User, false, null, null, "tenant-b", null, null, null, CancellationToken.None), okCtx);
        status.ShouldBe(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("rows")[0].GetProperty("queries").GetInt64().ShouldBe(4);
    }

    [Fact]
    public async Task Data_owner_sees_owned_table_and_forbidden_on_unowned_table()
    {
        var ownedTable = TableIdentifier.Parse("default.sales.orders");
        var harness = new ReportHarness();
        harness.Ownership.Owned[(ownedTable, OwnerSid)] = true;
        harness.Repo.Rows.Add(MakeRow(Today, "pg-main", tableId: "default.sales.orders", allowCount: 5, queries: 5));

        var okCtx = harness.CreateHttpContext("Consumer", "tenant-a", OwnerSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleTableReportAsync("default.sales.orders", okCtx, harness.Service, harness.Options, null, null, null, null, null, null, null, CancellationToken.None), okCtx))
            .Status.ShouldBe(StatusCodes.Status200OK);

        var deniedCtx = harness.CreateHttpContext("Consumer", "tenant-a", OwnerSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleTableReportAsync("default.hr.salaries", deniedCtx, harness.Service, harness.Options, null, null, null, null, null, null, null, CancellationToken.None), deniedCtx))
            .Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Data_owner_cannot_see_user_breakdown_unless_enabled()
    {
        var ownedTable = TableIdentifier.Parse("default.sales.orders");
        var harnessDenied = new ReportHarness(dataOwnersSeeUsers: false);
        harnessDenied.Ownership.Owned[(ownedTable, OwnerSid)] = true;
        var deniedCtx = harnessDenied.CreateHttpContext("Consumer", "tenant-a", OwnerSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleTableReportAsync("default.sales.orders", deniedCtx, harnessDenied.Service, harnessDenied.Options, null, null, "user", null, null, null, null, CancellationToken.None), deniedCtx))
            .Status.ShouldBe(StatusCodes.Status403Forbidden);

        var harnessAllowed = new ReportHarness(dataOwnersSeeUsers: true);
        harnessAllowed.Ownership.Owned[(ownedTable, OwnerSid)] = true;
        var okCtx = harnessAllowed.CreateHttpContext("Consumer", "tenant-a", OwnerSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleTableReportAsync("default.sales.orders", okCtx, harnessAllowed.Service, harnessAllowed.Options, null, null, "user", null, null, null, null, CancellationToken.None), okCtx))
            .Status.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Grantee_sees_only_own_grant_report()
    {
        var userGrantId = Guid.NewGuid();
        var groupGrantId = Guid.NewGuid();
        var otherGrantId = Guid.NewGuid();

        var harness = new ReportHarness();
        harness.Consents.Items[userGrantId] = MakeConsent(userGrantId, "tenant-a", "default.sales.orders", GranteeType.User, AliceSid);
        harness.Consents.Items[groupGrantId] = MakeConsent(groupGrantId, "tenant-a", "default.sales.orders", GranteeType.Group, GroupSid);
        harness.Consents.Items[otherGrantId] = MakeConsent(otherGrantId, "tenant-a", "default.sales.orders", GranteeType.User, BobSid);

        harness.Repo.GrantRows.Add(MakeGrantRow(Today, "pg-main", userGrantId.ToString(), queries: 2));
        harness.Repo.GrantRows.Add(MakeGrantRow(Today, "pg-main", groupGrantId.ToString(), queries: 4));

        var userCtx = harness.CreateHttpContext("Consumer", "tenant-a", AliceSid.Value, [GroupSid.Value]);
        (await ExecuteAsync(await UsageEndpoints.HandleGrantReportAsync(userGrantId.ToString(), userCtx, harness.Service, null, null, null, null, null, null, CancellationToken.None), userCtx))
            .Status.ShouldBe(StatusCodes.Status200OK);

        var grpCtx = harness.CreateHttpContext("Consumer", "tenant-a", AliceSid.Value, [GroupSid.Value]);
        (await ExecuteAsync(await UsageEndpoints.HandleGrantReportAsync(groupGrantId.ToString(), grpCtx, harness.Service, null, null, null, null, null, null, CancellationToken.None), grpCtx))
            .Status.ShouldBe(StatusCodes.Status200OK);

        var otherCtx = harness.CreateHttpContext("Consumer", "tenant-a", AliceSid.Value, [GroupSid.Value]);
        (await ExecuteAsync(await UsageEndpoints.HandleGrantReportAsync(otherGrantId.ToString(), otherCtx, harness.Service, null, null, null, null, null, null, CancellationToken.None), otherCtx))
            .Status.ShouldBe(StatusCodes.Status403Forbidden);

        var missingCtx = harness.CreateHttpContext("Consumer", "tenant-a", AliceSid.Value, [GroupSid.Value]);
        (await ExecuteAsync(await UsageEndpoints.HandleGrantReportAsync(Guid.NewGuid().ToString(), missingCtx, harness.Service, null, null, null, null, null, null, CancellationToken.None), missingCtx))
            .Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Data_owner_sees_grant_report_only_for_owned_tables()
    {
        var ownedGrantId = Guid.NewGuid();
        var foreignTableGrantId = Guid.NewGuid();
        var ownedTable = TableIdentifier.Parse("default.sales.orders");

        var harness = new ReportHarness();
        harness.Ownership.Owned[(ownedTable, OwnerSid)] = true;
        harness.Consents.Items[ownedGrantId] = MakeConsent(ownedGrantId, "tenant-a", "default.sales.orders", GranteeType.User, BobSid);
        harness.Consents.Items[foreignTableGrantId] = MakeConsent(foreignTableGrantId, "tenant-a", "default.hr.salaries", GranteeType.User, BobSid);

        var ownedCtx = harness.CreateHttpContext("Consumer", "tenant-a", OwnerSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleGrantReportAsync(ownedGrantId.ToString(), ownedCtx, harness.Service, null, null, null, null, null, null, CancellationToken.None), ownedCtx))
            .Status.ShouldBe(StatusCodes.Status200OK);

        var foreignCtx = harness.CreateHttpContext("Consumer", "tenant-a", OwnerSid.Value);
        (await ExecuteAsync(await UsageEndpoints.HandleGrantReportAsync(foreignTableGrantId.ToString(), foreignCtx, harness.Service, null, null, null, null, null, null, CancellationToken.None), foreignCtx))
            .Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    private static void AssertPolicy(List<RouteEndpoint> endpoints, string pattern, string expectedPolicy, string method = "GET")
    {
        var ep = endpoints.Single(e =>
            string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal)
            && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));
        var auth = ep.Metadata.GetMetadata<IAuthorizeData>();
        auth.ShouldNotBeNull();
        auth.Policy.ShouldBe(expectedPolicy);
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return (ctx.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static ClaimsPrincipal BuildPrincipal(string role, string tenantId, string sid, IReadOnlyList<string>? groupSids = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("sub", sid),
            new("tenant_id", tenantId),
            new(ClaimTypes.Role, role)
        };
        if (groupSids is { Count: > 0 })
        {
            foreach (string g in groupSids)
            {
                claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(g));
            }

            claims.Add(GroupResolutionClaims.CreateMarker());
            claims.Add(GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static Consent MakeConsent(Guid id, string tenant, string table, GranteeType granteeType, Sid granteeSid) => new()
    {
        Id = id,
        TenantId = new TenantId(tenant),
        TableIdentifier = TableIdentifier.Parse(table),
        GranteeType = granteeType,
        GranteeSid = granteeSid,
        ValidFrom = Now.AddDays(-5),
        ValidTo = Now.AddDays(30)
    };

    private static UsageReportRow MakeRow(
        DateOnly day, string dataSource, string tenant = "tenant-a", string? userKey = null,
        string? tableId = null, long allowCount = 1, long queries = 1) =>
        new(day, userKey, null, null, dataSource, tableId, null, null, null, null,
            allowCount, queries, 10, 1, 1024, 50, 0, 20, 1, 0, 0, allowCount, 0, 0, 0, 0, 0, 0);

    private static UsageReportRow MakeGrantRow(DateOnly day, string dataSource, string grantId, long queries) =>
        new(day, null, null, null, dataSource, null, null, null, null, grantId,
            queries, queries, 10, 0, 1024, 0, 0, 20, 0, 0, 0, queries, 0, 0, 0, 0, 0, 0);

    private static UsageTariffVersion MakeTariff(string tenant, string ds) =>
        new(tenant, ds, 1, Today.AddDays(-30), null, 1m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "seed", Now, "seed");

    private sealed class ReportHarness
    {
        public FakeUsageRepository Repo { get; } = new();
        public FakeOwnershipRepository Ownership { get; } = new();
        public FakeConsentRepository Consents { get; } = new();
        public IOptions<UsageAccountingOptions> Options { get; }
        public UsageReportService Service { get; }
        private readonly ServiceProvider _sp;

        public ReportHarness(bool dataOwnersSeeUsers = false)
        {
            var time = new FakeTimeProvider(Now);
            Options = Microsoft.Extensions.Options.Options.Create(new UsageAccountingOptions { DataOwnersSeeUsers = dataOwnersSeeUsers });
            Service = new UsageReportService(Repo, new UsageGroupNameResolver(Options, timeProvider: time), new NoOpAuditWriter(), Options, time);
            _sp = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IContainerOwnership>(Ownership)
                .AddSingleton<IConsentRepository>(Consents)
                .BuildServiceProvider();
        }

        public DefaultHttpContext CreateHttpContext(string role, string tenantId, string sid, IReadOnlyList<string>? groupSids = null)
        {
            var ctx = new DefaultHttpContext
            {
                User = BuildPrincipal(role, tenantId, sid, groupSids),
                RequestServices = _sp
            };
            ctx.Response.Body = new MemoryStream();
            return ctx;
        }
    }

    private sealed class RecordingRouteBuilder(IServiceProvider sp) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => sp;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotSupportedException();
    }

    private sealed class NoOpAuditWriter : IAuditWriter
    {
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeUsageRepository : IUsageRepository
    {
        public List<UsageReportRow> Rows { get; } = [];
        public List<UsageReportRow> GrantRows { get; } = [];
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
        public Task<UsageQuotaTenantSnapshot> GetQuotaSnapshotAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
            Task.FromResult(UsageQuotaTenantSnapshot.Empty);
        public Task<IReadOnlyList<TenantId>> GetActiveUsageTenantsAsync(DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantId>>([]);

        public Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default)
        {
            var source = query.GroupBy == UsageGroupBy.Grant ? GrantRows : Rows;
            var filtered = new List<UsageReportRow>();
            foreach (var r in source)
            {
                if (query.UserKey is not null && !string.Equals(r.UserKey, query.UserKey, StringComparison.Ordinal)) continue;
                if (query.TableId is not null && !string.Equals(r.TableId, query.TableId, StringComparison.Ordinal)) continue;
                if (query.GrantId is not null && !string.Equals(r.GrantId, query.GrantId, StringComparison.Ordinal)) continue;
                filtered.Add(new UsageReportRow(
                    r.DayUtc ?? query.FromUtc,
                    query.GroupBy == UsageGroupBy.User ? (r.UserKey ?? AliceSid.Value) : r.UserKey,
                    r.GroupSid, r.BillingGroup, r.DataSource, r.TableId, r.Transport, r.Application, r.ClientId, r.GrantId,
                    r.Requests, r.Queries, r.RowsReturned, r.RowsMeasured, r.Bytes, r.DurationMs, r.AnalysisCost,
                    r.SourceComputeMs, r.SourceComputeMeasured, r.SourceCostMicros, r.SourceCostMeasured,
                    r.AllowCount, r.DenyAccess, r.DenyRateLimit, r.DenyCost, r.DenyQuota, r.DenyOther, r.Errors));
            }

            return Task.FromResult(new UsageReportPage(filtered, null));
        }
    }

    private sealed class FakeOwnershipRepository : IContainerOwnership
    {
        public Dictionary<(TableIdentifier, Sid), bool> Owned { get; } = new();
        public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation d, CancellationToken ct = default) => Task.FromResult(d);
        public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier t, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DataOwner>>([]);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
            Task.FromResult(Owned.TryGetValue((table, approverSid), out bool ok) && ok);
    }

    private sealed class FakeConsentRepository : IConsentRepository
    {
        public Dictionary<Guid, Consent> Items { get; } = new();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) =>
            Task.FromResult(Items.TryGetValue(consentId, out var c) ? c : null);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier t, DateTimeOffset at, TenantId? tid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? r, DateTimeOffset? at, TenantId? tid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent> CreateConsentAsync(Consent c, CancellationToken ct = default) => Task.FromResult(c);
        public Task RevokeConsentAsync(Guid id, Sid by, string reason, ConsentRevocationAuthority auth, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid rid, DateTimeOffset at, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid id, Guid rid, Sid by, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery q, CancellationToken ct = default) =>
            Task.FromResult(new Autheris.Application.Governance.Paging.ConsentPage([], null));
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> rev, IReadOnlyList<Consent> cr, Sid act, string rsn, ConsentRevocationAuthority a, IReadOnlyList<AuditLogEntry>? au = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
