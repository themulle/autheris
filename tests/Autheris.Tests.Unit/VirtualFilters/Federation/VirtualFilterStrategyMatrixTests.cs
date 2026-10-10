namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.VirtualFilterEnforcementFixture;

/// <summary>
/// F-GOV-14 WI-8 (M5, M7, AC-6.1, ST-25, ST-28, ST-29, ST-32): every cell of the strategy matrix (4 strategies x 5
/// situations) yields the expected tier, skip decision, pushed values or denial code. Only <c>queried ∩ authorized</c>
/// is ever pushed, and everything the endpoint cannot take safely falls back to Tier 3 or denies.
/// </summary>
public sealed class VirtualFilterStrategyMatrixTests
{
    private static readonly FastSqlEngine Engine = new();
    private const string Scan = "SELECT c.id FROM crm_api.public.customers c";
    private const string Miss = Scan + " WHERE c.id = '2'";
    private const string Hit = Scan + " WHERE c.id IN ('1', '2')";

    private static TableMetadata Endpoint(
        HttpBatchType batch = HttpBatchType.QueryParameterList,
        string? param = "ids",
        string path = "/customers",
        HttpPaginationConfig? paging = null,
        string idType = "varchar")
    {
        var table = Customers(idType);
        return WithEndpoint(table, new HttpEndpointDescriptor
            {
                BaseUrl = "https://api.example.internal",
                PathTemplate = path,
                BatchType = batch,
                BatchParamName = param,
                PrimaryKeyField = "id",
                Pagination = paging,
                CompleteResponse = true
            });
    }

    private static AppliedVirtualFilterBinding Pair(
        VirtualFilterExecutionStrategy strategy,
        PushdownParameterFormat? format = null,
        int maxKeys = 100,
        string filterName = "vf_customers") =>
        new(KeyFilter(filterName, "client_id"),
            new FilterBinding
            {
                FilterName = filterName,
                TargetPattern = "crm_api.public.customers",
                ColumnMap = new Dictionary<string, string> { ["client_id"] = "id" },
                Strategy = strategy,
                PushdownFormat = format,
                MaxPushdownKeys = maxKeys
            },
            1);

    private static async Task<VirtualFilterTablePlan> PlanAsync(
        string sql,
        TableMetadata table,
        IReadOnlyList<AppliedVirtualFilterBinding> pairs,
        FakeKeySetProvider keys,
        VirtualFilterFederationOptions? options = null)
    {
        var metadata = Engine.Analyze(sql.AsMemory());
        var tables = metadata.ReferencedTables.Select(t => new ResolvedSourceTable(t, table.Identifier, table, table.Identifier.Domain)).ToList();
        var (tree, _) = Engine.Parse(sql.AsMemory());
        var decision = Unrestricted(table).WithMandatoryPredicate("EXISTS (SELECT 1 FROM vf)", pairs.Select(p => p.Filter.Name).Distinct().ToList(), pairs);
        var planner = new VirtualFilterFederationPlanner(keys, Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { Federation = options ?? new() } }));
        var plans = await planner.PlanAsync(new SqlAstBuilder().BuildStatement(tree), tables, new Dictionary<TableIdentifier, TableAccessDecision> { [table.Identifier] = decision }, Tenant, CancellationToken.None);
        return plans.ShouldHaveSingleItem().Value;
    }

    private static string[]? Pushed(VirtualFilterTablePlan plan, string param = "ids") =>
        plan.PushdownArguments is { } args ? ((IEnumerable<object>)args[param]!).Select(v => (string)v).ToArray() : null;

    private static FakeKeySetProvider Keys(params string[] ids) =>
        new FakeKeySetProvider().With("vf_customers", ids.Select(i => new object?[] { i }).ToArray());

    public static IEnumerable<object?[]> Cells()
    {
        var s = VirtualFilterExecutionStrategy.Adaptive;
        yield return [s, "empty", VirtualFilterTier.EmptyKeySet, true, null, null];
        yield return [s, "miss", VirtualFilterTier.PointLookup, true, null, null];
        yield return [s, "hit", VirtualFilterTier.PointLookup, false, new[] { "1" }, null];
        yield return [s, "scan", VirtualFilterTier.Pushdown, false, new[] { "1", "3" }, null];
        yield return [s, "over", VirtualFilterTier.FullFetch, false, null, null];

        s = VirtualFilterExecutionStrategy.DuckDbHashJoin;
        yield return [s, "empty", VirtualFilterTier.EmptyKeySet, true, null, null];
        yield return [s, "miss", VirtualFilterTier.PointLookup, true, null, null];
        yield return [s, "hit", VirtualFilterTier.FullFetch, false, null, null];
        yield return [s, "scan", VirtualFilterTier.FullFetch, false, null, null];
        yield return [s, "over", VirtualFilterTier.FullFetch, false, null, null];

        s = VirtualFilterExecutionStrategy.ShortCircuitOnly;
        yield return [s, "empty", VirtualFilterTier.EmptyKeySet, true, null, null];
        yield return [s, "miss", VirtualFilterTier.PointLookup, true, null, null];
        yield return [s, "hit", VirtualFilterTier.PointLookup, false, new[] { "1" }, null];
        yield return [s, "scan", null, false, null, "VF_SCAN_NOT_PERMITTED"];
        yield return [s, "over", null, false, null, "VF_SCAN_NOT_PERMITTED"];

        s = VirtualFilterExecutionStrategy.PushdownOnly;
        yield return [s, "empty", VirtualFilterTier.EmptyKeySet, true, null, null];
        yield return [s, "miss", VirtualFilterTier.PointLookup, true, null, null];
        yield return [s, "hit", VirtualFilterTier.PointLookup, false, new[] { "1" }, null];
        yield return [s, "scan", VirtualFilterTier.Pushdown, false, new[] { "1", "3" }, null];
        yield return [s, "over", null, false, null, "VF_PUSHDOWN_LIMIT_EXCEEDED"];
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Cell_YieldsTheExpectedTierPushdownOrDenial(
        VirtualFilterExecutionStrategy strategy, string situation, VirtualFilterTier? tier, bool skip, string[]? pushed, string? code)
    {
        var (sql, keys, options) = situation switch
        {
            "empty" => (Scan, Keys(), new VirtualFilterFederationOptions()),
            "miss" => (Miss, Keys("1", "3"), new VirtualFilterFederationOptions()),
            "hit" => (Hit, Keys("1", "3"), new VirtualFilterFederationOptions()),
            "scan" => (Scan, Keys("1", "3"), new VirtualFilterFederationOptions()),
            _ => (Scan, Keys("1", "2", "3", "4", "5"), new VirtualFilterFederationOptions { MaxPushdownKeys = 2 })
        };

        if (code != null)
        {
            var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(sql, Endpoint(), [Pair(strategy)], keys, options));
            ex.ErrorCode.ShouldBe(code);
            return;
        }

        var plan = await PlanAsync(sql, Endpoint(), [Pair(strategy)], keys, options);

        plan.Tier.ShouldBe(tier!.Value);
        plan.SkipUpstream.ShouldBe(skip);
        Pushed(plan).ShouldBe(pushed);
    }

    [Fact]
    public async Task Hit_PushesTheIntersectionNotTheWholeSet_AndNeverAnUnauthorizedKey()
    {
        // AC-6.3 / ST-25: id IN ('1','2','99') with {1,3} authorized pushes exactly ['1'].
        var plan = await PlanAsync(Scan + " WHERE c.id IN ('1', '2', '99')", Endpoint(), [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("1", "3"));

        Pushed(plan).ShouldBe(["1"]);
    }

    [Fact]
    public async Task Pushdown_ArgumentIsTheBatchParameter_AsAListOfInvariantText()
    {
        var plan = await PlanAsync(Scan, Endpoint(idType: "bigint"), [Pair(VirtualFilterExecutionStrategy.Adaptive)], new FakeKeySetProvider().With("vf_customers", [102L], [101L]));

        Pushed(plan).ShouldBe(["101", "102"]);
        plan.PushdownArguments!["ids"].ShouldBeOfType<List<string>>();
    }

    [Theory]
    [InlineData(PushdownParameterFormat.RepeatedParam)]
    [InlineData(PushdownParameterFormat.ODataIn)]
    public async Task Adaptive_FormatUnsupported_Tier3(PushdownParameterFormat format)
    {
        var plan = await PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.Adaptive, format)], Keys("1"));

        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
        plan.Reason.ShouldBe("format_unsupported");
        plan.PushdownArguments.ShouldBeNull();
    }

    [Fact]
    public async Task PushdownOnly_FormatUnsupported_Denied()
    {
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.PushdownOnly, PushdownParameterFormat.ODataIn)], Keys("1")));

        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_UNAVAILABLE");
    }

    [Theory]
    [InlineData(PushdownParameterFormat.CommaSeparated, HttpBatchType.QueryParameterList, "/customers", true)]
    [InlineData(PushdownParameterFormat.PostBatch, HttpBatchType.JsonBodyArray, "/customers", true)]
    [InlineData(PushdownParameterFormat.PostBatch, HttpBatchType.QueryParameterList, "/customers", false)]
    [InlineData(PushdownParameterFormat.CommaSeparated, HttpBatchType.JsonBodyArray, "/customers", false)]
    public async Task AssertedFormat_MustMatchTheEndpointCapability(PushdownParameterFormat format, HttpBatchType batch, string path, bool pushed)
    {
        var plan = await PlanAsync(Scan, Endpoint(batch, path: path), [Pair(VirtualFilterExecutionStrategy.Adaptive, format)], Keys("1"));

        (plan.PushdownArguments != null).ShouldBe(pushed);
    }

    [Fact]
    public async Task Adaptive_UrlTooLong_Tier3()
    {
        var ids = Enumerable.Range(1000, 60).Select(i => i.ToString()).ToArray();

        var plan = await PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys(ids), new VirtualFilterFederationOptions { MaxPushdownUrlLength = 256 });

        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
        plan.Reason.ShouldBe("url_too_long");
    }

    [Fact]
    public async Task PushdownOnly_UrlTooLong_DeniedLimitExceeded()
    {
        var ids = Enumerable.Range(1000, 60).Select(i => i.ToString()).ToArray();

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.PushdownOnly)], Keys(ids), new VirtualFilterFederationOptions { MaxPushdownUrlLength = 256 }));

        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task BindingMaxPushdownKeys_LowersTheLimit()
    {
        var plan = await PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.Adaptive, maxKeys: 2)], Keys("1", "2", "3"));

        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
        plan.Reason.ShouldBe("key_count_over_limit");
    }

    [Fact]
    public async Task CommaInValue_NoQueryParameterListPushdown()
    {
        var plan = await PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("a,b", "c"));

        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
        plan.PushdownArguments.ShouldBeNull();
    }

    [Fact]
    public async Task CommaInValue_JsonBodyArray_IsPushed()
    {
        var plan = await PlanAsync(Scan, Endpoint(HttpBatchType.JsonBodyArray), [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("a,b", "c"));

        Pushed(plan).ShouldBe(["a,b", "c"]);
    }

    [Fact]
    public async Task PathLookup_UnsafeKeys_AreNeverPushed_AdaptiveDeniedUnfillable_PushdownOnlyDenied()
    {
        // ST-29: 'A/B', '..' and '' on a PathLookup table. CR-01: the template cannot be filled, so no full scan either.
        var table = Endpoint(HttpBatchType.ParallelSingleRequests, path: "/api/v1/customers/{id}");
        foreach (var unsafeKey in new[] { "A/B", "..", "" })
        {
            var keys = Keys("1", unsafeKey);
            var adaptive = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.Adaptive)], keys));
            adaptive.ErrorCode.ShouldBe("VF_PATH_TEMPLATE_UNFILLABLE");
            var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.PushdownOnly)], keys));
            ex.ErrorCode.ShouldBe("VF_PUSHDOWN_UNAVAILABLE");
        }
    }

    [Fact]
    public async Task PathLookup_MoreKeysThanMaxRequests_AdaptiveDeniedUnfillable_PushdownOnlyDeniedLimitExceeded()
    {
        // ST-32 and CR-01: over the path-lookup budget is denied, never read as a full scan.
        var table = Endpoint(HttpBatchType.ParallelSingleRequests, path: "/api/v1/customers/{id}");
        var keys = Keys(Enumerable.Range(1, 4).Select(i => i.ToString()).ToArray());
        var options = new VirtualFilterFederationOptions { MaxPathLookupRequests = 3 };

        var adaptive = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.Adaptive)], keys, options));
        adaptive.ErrorCode.ShouldBe("VF_PATH_TEMPLATE_UNFILLABLE");
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.PushdownOnly)], keys, options));
        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_LIMIT_EXCEEDED");
        Pushed(await PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.Adaptive)], keys, new VirtualFilterFederationOptions { MaxPathLookupRequests = 4 })).ShouldBe(["1", "2", "3", "4"]);
    }

    [Fact]
    public async Task PathLookup_DuckDbHashJoin_WantsAFullRead_IsDeniedBecauseTheTemplateCannotBeFilled()
    {
        var table = Endpoint(HttpBatchType.ParallelSingleRequests, path: "/api/v1/customers/{id}");

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.DuckDbHashJoin)], Keys("1", "2")));

        ex.ErrorCode.ShouldBe("VF_PATH_TEMPLATE_UNFILLABLE");
    }

    [Fact]
    public async Task PathLookup_PushdownSwitchOff_DeniesInsteadOfScanning()
    {
        var table = Endpoint(HttpBatchType.ParallelSingleRequests, path: "/api/v1/customers/{id}");

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("1"), new VirtualFilterFederationOptions { PushdownEnabled = false }));

        ex.ErrorCode.ShouldBe("VF_PATH_TEMPLATE_UNFILLABLE");
    }

    [Fact]
    public async Task PathLookup_EmptyAndMissCases_StillSkipTheUpstream_NoDenial()
    {
        var table = Endpoint(HttpBatchType.ParallelSingleRequests, path: "/api/v1/customers/{id}");

        var empty = await PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys());
        var miss = await PlanAsync(Miss, table, [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("1"));

        empty.SkipUpstream.ShouldBeTrue();
        miss.SkipUpstream.ShouldBeTrue();
    }

    [Fact]
    public async Task PathLookup_Plan_CarriesTheLookupSpec_OtherKindsDoNot()
    {
        var path = await PlanAsync(Scan, Endpoint(HttpBatchType.ParallelSingleRequests, path: "/api/v1/customers/{id}"), [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("2", "1"));
        var qpl = await PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("1"));

        path.PathLookup.ShouldNotBeNull();
        path.PathLookup!.Argument.ShouldBe("id");
        path.PathLookup.Column.ShouldBe("id");
        path.PathLookup.KeyType.ShouldBe(VirtualFilterKeyType.Text);
        path.PathLookup.Values.ShouldBe(["1", "2"]);
        qpl.PathLookup.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpBatchType.QueryParameterList, "/customers/{region}/items")]
    [InlineData(HttpBatchType.ParallelSingleRequests, "/regions/{region}/customers/{id}")]
    public async Task AnyPlaceholderThePlanCannotFill_IsDenied(HttpBatchType batch, string path)
    {
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanAsync(Scan, Endpoint(batch, path: path), [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("1")));

        ex.ErrorCode.ShouldBe("VF_PATH_TEMPLATE_UNFILLABLE");
    }

    [Theory]
    [InlineData(HttpBatchType.QueryParameterList, null)]
    [InlineData(HttpBatchType.None, "ids")]
    [InlineData(HttpBatchType.QueryParameterList, "tenant_id")]
    public async Task NoUsableCapability_AdaptiveFullFetch_PushdownOnlyDenied(HttpBatchType batch, string? param)
    {
        // ST-28
        var table = Endpoint(batch, param);

        (await PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("1"))).Tier.ShouldBe(VirtualFilterTier.FullFetch);
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.PushdownOnly)], Keys("1")));
        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_UNAVAILABLE");
    }

    [Fact]
    public async Task PagedEndpoint_NoPushdown()
    {
        var table = Endpoint(paging: new HttpPaginationConfig(HttpPaginationStrategy.PageNumber));

        var plan = await PlanAsync(Scan, table, [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("1"));

        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
    }

    [Fact]
    public async Task PushdownDisabledOption_AlwaysTier3_PushdownOnlyDenied()
    {
        var options = new VirtualFilterFederationOptions { PushdownEnabled = false };

        (await PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.Adaptive)], Keys("1"), options)).Tier.ShouldBe(VirtualFilterTier.FullFetch);
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanAsync(Scan, Endpoint(), [Pair(VirtualFilterExecutionStrategy.PushdownOnly)], Keys("1"), options));
        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_UNAVAILABLE");
    }

    [Fact]
    public async Task MixedStrategies_MostRestrictiveWins()
    {
        // PushdownOnly + DuckDbHashJoin contradict each other: deny instead of choosing one.
        var pairs = new[]
        {
            Pair(VirtualFilterExecutionStrategy.PushdownOnly, filterName: "vf_a"),
            Pair(VirtualFilterExecutionStrategy.DuckDbHashJoin, filterName: "vf_b")
        };
        var keys = new FakeKeySetProvider().With("vf_a", ["1"]).With("vf_b", ["1"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(Scan, Endpoint(), pairs, keys));

        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_UNAVAILABLE");
    }

    [Fact]
    public async Task TwoPairsOnTheKeyColumn_PushTheIntersection_DisjointSkipsTheUpstream()
    {
        var pairs = new[]
        {
            Pair(VirtualFilterExecutionStrategy.Adaptive, filterName: "vf_a"),
            Pair(VirtualFilterExecutionStrategy.Adaptive, filterName: "vf_b")
        };

        var overlapping = await PlanAsync(Scan, Endpoint(), pairs, new FakeKeySetProvider().With("vf_a", ["1"], ["2"], ["3"]).With("vf_b", ["2"], ["3"], ["4"]));
        var disjoint = await PlanAsync(Scan, Endpoint(), pairs, new FakeKeySetProvider().With("vf_a", ["1"]).With("vf_b", ["2"]));

        Pushed(overlapping).ShouldBe(["2", "3"]);
        disjoint.SkipUpstream.ShouldBeTrue();
        disjoint.Tier.ShouldBe(VirtualFilterTier.EmptyKeySet);
    }

    [Fact]
    public async Task PairNotOnThePrimaryKeyColumn_IsNotPushed_ButStillEnforced()
    {
        var table = Endpoint();
        var other = new AppliedVirtualFilterBinding(
            KeyFilter("vf_customers", "client_id"),
            new FilterBinding
            {
                FilterName = "vf_customers",
                TargetPattern = "crm_api.public.customers",
                ColumnMap = new Dictionary<string, string> { ["client_id"] = "account_id" }
            },
            1);

        var plan = await PlanAsync(Scan, table, [other], Keys("Z"));

        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
        plan.PushdownArguments.ShouldBeNull();
        plan.Enforcement.Apply([Customer("1")]).ShouldBeEmpty();
    }
}
