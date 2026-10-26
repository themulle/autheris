namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Claims;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-6 (ST-10, ST-22..ST-24, ST-27, ST-37): the real WebSQL cross-source pipeline (consent policy, Virtual
/// Filter resolver, router, planner, staging, DuckDB) enforces the authorized key set on a protected HTTP table in every
/// query shape; the upstream returns everything and only the authorized rows come back.
/// </summary>
public sealed class FederatedVirtualFilterEnforcementTests
{
    private static readonly TableMetadata Crm = HttpTable("crm_api", "public", "customers", "id", "name", "status");
    private static readonly TableMetadata Erp = SqlTable("erp", "public", "orders", "id", "customer_id");

    private const string Join = "SELECT c.id, c.name FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id";

    private static (StubTableConnector Crm, StubTableConnector Erp, FederatedServiceHarness.Pipeline Pipeline) Setup(
        VirtualFilterSnapshot? snapshot = null,
        Action<FakeKeySetProvider>? keys = null,
        int? timeoutSeconds = null,
        int? maxRows = null,
        TableMetadata? crm = null)
    {
        var crmTable = crm ?? Crm;
        var crmStub = new StubTableConnector("crm_api",
        [
            Row(("id", "1"), ("name", "Alpha"), ("status", "OPEN")),
            Row(("id", "2"), ("name", "Beta"), ("status", "OPEN")),
            Row(("id", "3"), ("name", "Gamma"), ("status", "OPEN")),
            Row(("id", "4"), ("name", "Delta"), ("status", "OPEN"))
        ]);
        var erpStub = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "1")), Row(("id", "11"), ("customer_id", "2")), Row(("id", "12"), ("customer_id", "3"))]);
        var provider = new FakeKeySetProvider().With("vf_customers", ["1"], ["3"]);
        keys?.Invoke(provider);
        var pipeline = Build(
            [(crmTable, crmStub), (Erp, erpStub)],
            Consents(Allow(crmTable.Identifier), Allow(Erp.Identifier)),
            snapshot ?? AliceSnapshot("crm_api.*.*", "crm_api.public.customers"),
            provider,
            timeoutSeconds: timeoutSeconds,
            maxStagedRowsPerTable: maxRows);
        return (crmStub, erpStub, pipeline);
    }

    private static async Task<List<string>> Ids(FederatedServiceHarness.Pipeline pipeline, string sql, string column = "id") =>
        (await pipeline.RunAsync(sql)).Select(r => Convert.ToString(r[column])!).OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Scan_UpstreamReturnsEverything_OnlyAuthorizedRows()
    {
        var (crm, _, pipeline) = Setup();

        (await Ids(pipeline, Join)).ShouldBe(["1", "3"]);
        crm.UpstreamCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Scan_SelectStar_ColumnsEqualHttpTableColumns()
    {
        var (_, _, pipeline) = Setup();

        var rows = await pipeline.RunAsync("SELECT c.* FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id");

        rows.Count.ShouldBe(2);
        rows[0].Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(["id", "name", "status"]);
    }

    [Fact]
    public async Task Scan_DuplicateKeyTuples_CountStarEqualsAuthorizedRowCount()
    {
        var (_, _, pipeline) = Setup(keys: k => k.With("vf_customers", ["1"], ["1"], ["3"], ["3"]));

        var rows = await pipeline.RunAsync("SELECT COUNT(*) AS n FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id");

        Convert.ToInt64(rows.ShouldHaveSingleItem()["n"]).ShouldBe(2);
    }

    [Theory]
    [InlineData("SELECT id FROM crm_api.public.customers UNION ALL SELECT id FROM erp.public.orders")]
    [InlineData("WITH c AS (SELECT id FROM crm_api.public.customers) SELECT c.id FROM c UNION ALL SELECT id FROM erp.public.orders")]
    [InlineData("SELECT x.id FROM (SELECT id FROM crm_api.public.customers) x UNION ALL SELECT id FROM erp.public.orders")]
    public async Task UnionCteSubquery_OverProtectedTable_OnlyAuthorizedRows(string sql)
    {
        // ST-27: ids 1 and 3 are authorized; the SQL table contributes 10, 11, 12.
        var (_, _, pipeline) = Setup();

        (await Ids(pipeline, sql)).ShouldBe(["1", "10", "11", "12", "3"]);
    }

    [Fact]
    public async Task SelfJoin_OverProtectedTable_OnlyAuthorizedRows()
    {
        var (crm, _, pipeline) = Setup();

        var ids = await Ids(pipeline,
            "SELECT a.id FROM crm_api.public.customers a JOIN crm_api.public.customers b ON a.id = b.id JOIN erp.public.orders o ON o.customer_id = a.id");

        ids.ShouldBe(["1", "3"]);
        crm.UpstreamCalls.ShouldBe(2);   // each occurrence is staged and enforced on its own
    }

    [Fact]
    public async Task EmptyKeySet_ConnectorNeverCalled_ZeroRows_AndTheSqlSideStillExecutes()
    {
        var (crm, erp, pipeline) = Setup(keys: k => k.With("vf_customers"));

        (await pipeline.RunAsync(Join)).ShouldBeEmpty();
        crm.UpstreamCalls.ShouldBe(0);
        erp.UpstreamCalls.ShouldBe(1);
    }

    [Fact]
    public async Task KeySourceUnavailable_Denied_ConnectorNeverCalled_NoInnerMessageInTheAudit()
    {
        var (crm, erp, pipeline) = Setup(keys: k => k.Failure = new InvalidOperationException("secret-inner-detail 4711"));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Join));

        ex.ErrorCode.ShouldBe("VIRTUAL_FILTER_SOURCE_UNAVAILABLE");
        ex.Message.ShouldNotContain("secret-inner-detail");
        crm.UpstreamCalls.ShouldBe(0);
        erp.UpstreamCalls.ShouldBe(0);
        await pipeline.Audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.WebSqlQueryDenied &&
                                       e.DetailsJson!.Contains("\"reasonCode\":\"VIRTUAL_FILTER_SOURCE_UNAVAILABLE\"") &&
                                       !e.DetailsJson.Contains("secret-inner-detail")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeySourceTimesOut_DeniedWithinTheRequestTimeout_ConnectorNeverCalled()
    {
        // ST-23: the provider ignores the token; the request timeout (1 s) still bounds key loading.
        var (crm, _, pipeline) = Setup(keys: k => { k.Delay = TimeSpan.FromSeconds(60); k.IgnoreCancellation = true; }, timeoutSeconds: 1);

        // The provider would block for 60 s: the pipeline only returns (inside the hang guard) if it enforces the request timeout itself.
        var started = DateTime.UtcNow;
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Join)).WaitAsync(WaitHelper.HangGuard);
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10)); // loose: 10x the 1 s timeout, far below the 60 s provider delay

        ex.ErrorCode.ShouldBe("VIRTUAL_FILTER_SOURCE_UNAVAILABLE");
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ShortCircuitOnlyScan_DeniedScanNotPermitted_NoUpstreamCall()
    {
        var snapshot = AliceSnapshot("crm_api.*.*", "crm_api.public.customers");
        var profile = snapshot.Profiles[0];
        snapshot = snapshot with
        {
            Profiles = [profile with { Bindings = [profile.Bindings[0] with { Strategy = VirtualFilterExecutionStrategy.ShortCircuitOnly }] }]
        };
        var (crm, _, pipeline) = Setup(snapshot);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Join));

        ex.ErrorCode.ShouldBe("VF_SCAN_NOT_PERMITTED");
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task UpstreamOverMaxStagedRows_Denied_NoTruncation()
    {
        var (_, _, pipeline) = Setup(maxRows: 3);   // the upstream returns four rows before enforcement

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Join));

        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task EmptyKeySet_OnAForwardBearerTable_IsDeniedLikeAHit()
    {
        // ST-24: Tier 0 never turns a misconfigured table into a 200.
        var forward = Crm with
        {
            Table = new Table
            {
                TableName = "customers",
                SchemaName = "public",
                SourceType = "Http",
                DataSourceType = DataSourceType.HttpDeclarative,
                SourceName = "crm_api",
                IsActive = true,
                HttpEndpoint = new HttpEndpointDescriptor
                {
                    BaseUrl = "https://api.example.internal",
                    PathTemplate = "/customers",
                    AuthMode = HttpAuthMode.ForwardBearerToken,
                    CompleteResponse = true
                }
            }
        };
        var (crm, _, pipeline) = Setup(keys: k => k.With("vf_customers"), crm: forward);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Join));

        ex.ErrorCode.ShouldBe("AUTH_MODE_UNSUPPORTED");
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("id; DROP")]
    [InlineData("no_such_column")]
    public async Task ColumnMapTargetThatIsNotACatalogColumn_DeniedBindingMisconfigured_NoKeysNoUpstream(string target)
    {
        // ST-37: a profile written directly to the repository bypasses write-time validation.
        var snapshot = AliceSnapshot("crm_api.*.*", "crm_api.public.customers");
        var profile = snapshot.Profiles[0];
        var binding = profile.Bindings[0] with { ColumnMap = new Dictionary<string, string> { ["client_id"] = target } };
        snapshot = snapshot with { Profiles = [profile with { Bindings = [binding] }] };
        var (crm, _, pipeline) = Setup(snapshot);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Join));

        ex.ErrorCode.ShouldBe("VF_BINDING_MISCONFIGURED");
        crm.UpstreamCalls.ShouldBe(0);
        pipeline.KeySetProvider.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task OtherGranteesFilter_IsNeverLoaded()
    {
        // ST-10: Bob's profile binds the same table with another filter; Alice's query loads only Alice's filter.
        var snapshot = AliceSnapshot("crm_api.*.*", "crm_api.public.customers");
        var bobFilter = VirtualFilterModelTests.DavidFilter("vf_bob") with { TenantId = Tenant };
        var bobProfile = snapshot.Profiles[0] with
        {
            Name = "bob",
            GranteeSid = new Sid("S-1-5-21-VF-BOB"),
            Bindings = [snapshot.Profiles[0].Bindings[0] with { FilterName = "vf_bob" }]
        };
        snapshot = snapshot with { Filters = [.. snapshot.Filters, bobFilter], Profiles = [.. snapshot.Profiles, bobProfile] };
        var (_, _, pipeline) = Setup(snapshot, k => k.With("vf_bob", ["2"]));

        (await Ids(pipeline, Join)).ShouldBe(["1", "3"]);
        pipeline.KeySetProvider.CalledFilters.ShouldBe(["vf_customers"]);
    }

    // ----- WI-11: consent bypass without consent services still resolves Virtual Filters (SEC-19, ST-36) -----

    private static ClaimsPrincipal UserWithoutSid() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "nobody"), new Claim("tenant_id", Tenant.Value)], "Test"));

    private static (StubTableConnector Crm, FederatedServiceHarness.Pipeline Pipeline) BypassSetup(
        IMandatoryRowFilterResolver? resolver = null, VirtualFilterSnapshot? snapshot = null)
    {
        var crm = new StubTableConnector("crm_api",
            [Row(("id", "1"), ("name", "A"), ("status", "OPEN")), Row(("id", "2"), ("name", "B"), ("status", "OPEN")), Row(("id", "3"), ("name", "C"), ("status", "OPEN"))]);
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "1")), Row(("id", "11"), ("customer_id", "2")), Row(("id", "12"), ("customer_id", "3"))]);
        var provider = new FakeKeySetProvider().With("vf_customers", ["1"], ["3"]);
        var pipeline = Build(
            [(Crm, crm), (Erp, erp)],
            consents: null,
            snapshot ?? AliceSnapshot("crm_api.*.*", "crm_api.public.customers"),
            provider,
            consentBypass: true,
            resolverOverride: resolver);
        return (crm, pipeline);
    }

    [Fact]
    public async Task ConsentBypassWithoutConsentServices_VirtualFilterStillApplied()
    {
        var (crm, pipeline) = BypassSetup();

        (await Ids(pipeline, Join)).ShouldBe(["1", "3"]);
        crm.UpstreamCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ConsentBypassWithoutConsentServices_ResolverDenies_QueryDenied()
    {
        var resolver = Substitute.For<IMandatoryRowFilterResolver>();
        resolver.ResolveAsync(Arg.Any<MandatoryFilterQuery>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(MandatoryFilterOutcome.Deny("VF_BINDING_MISCONFIGURED: bad binding")));
        resolver.HasActiveProfilesAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        var (crm, pipeline) = BypassSetup(resolver);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync(Join));

        ex.ErrorCode.ShouldBe("VF_BINDING_MISCONFIGURED");
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ConsentBypass_CallerWithoutSid_AndAnActiveProfile_IsDeniedEnforcementUnavailable()
    {
        var (crm, pipeline) = BypassSetup();

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsAsync(UserWithoutSid(), Join));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_UNAVAILABLE");
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ConsentBypass_CallerWithoutSid_AndNoProfile_IsNotDeniedByVirtualFilters()
    {
        var (_, pipeline) = BypassSetup(snapshot: new VirtualFilterSnapshot(1, [], []));

        (await pipeline.RunAsAsync(UserWithoutSid(), "SELECT o.id FROM erp.public.orders o JOIN crm_api.public.customers c ON o.customer_id = c.id")).Rows.Count.ShouldBe(3);
    }
}
