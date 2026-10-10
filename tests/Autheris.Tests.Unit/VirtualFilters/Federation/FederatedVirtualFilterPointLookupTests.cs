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
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.VirtualFilterEnforcementFixture;

/// <summary>
/// F-GOV-14 WI-7 (M6, ST-25, ST-26): Tier 1. A point lookup whose queried keys are all unauthorized skips the upstream
/// call and stages an empty relation (the true answer of the user query); every other query is read as before and
/// filtered by the enforcement. A miss never changes the result, only what is fetched.
/// </summary>
public sealed class FederatedVirtualFilterPointLookupTests
{
    private static readonly FastSqlEngine Engine = new();
    private static readonly TableMetadata Crm = HttpTable("crm_api", "public", "customers", "id", "name", "status");
    private static readonly TableMetadata Erp = SqlTable("erp", "public", "orders", "id", "customer_id");

    private const string From = " FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id ";

    private static (StubTableConnector Crm, Pipeline Pipeline) Setup(
        VirtualFilterExecutionStrategy strategy = VirtualFilterExecutionStrategy.Adaptive,
        VirtualFilterFederationOptions? options = null,
        params string[] authorized)
    {
        var crmStub = new StubTableConnector("crm_api",
        [
            Row(("id", "1"), ("name", "Alpha"), ("status", "OPEN")),
            Row(("id", "2"), ("name", "Beta"), ("status", "OPEN")),
            Row(("id", "3"), ("name", "Gamma"), ("status", "OPEN"))
        ]);
        var erpStub = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "1")), Row(("id", "11"), ("customer_id", "2")), Row(("id", "12"), ("customer_id", "3"))]);
        var snapshot = AliceSnapshot("crm_api.*.*", "crm_api.public.customers");
        var profile = snapshot.Profiles[0];
        snapshot = snapshot with { Profiles = [profile with { Bindings = [profile.Bindings[0] with { Strategy = strategy }] }] };
        var keys = new FakeKeySetProvider().With("vf_customers", (authorized.Length == 0 ? ["1", "3"] : authorized).Select(a => new object?[] { a }).ToArray());
        var pipeline = Build(
            [(Crm, crmStub), (Erp, erpStub)],
            Consents(Allow(Crm.Identifier), Allow(Erp.Identifier)),
            snapshot,
            keys,
            federation: options);
        return (crmStub, pipeline);
    }

    private static async Task<List<string>> Ids(Pipeline pipeline, string sql, string column = "id") =>
        (await pipeline.RunAsync(sql)).Select(r => Convert.ToString(r[column])!).OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Miss_ZeroRows_ConnectorNeverCalled_SameColumnsAsEmptyMatch()
    {
        // ST-26: a miss and an authorized-but-nonexistent key look the same to the caller.
        var (missCrm, miss) = Setup();
        var (existsCrm, exists) = Setup(authorized: ["1", "3", "99"]);

        var missResult = await miss.RunFullAsync("SELECT c.id, c.name" + From + "WHERE c.id = '99'");
        var emptyMatch = await exists.RunFullAsync("SELECT c.id, c.name" + From + "WHERE c.id = '99'");

        missResult.RowCount.ShouldBe(0);
        emptyMatch.RowCount.ShouldBe(0);
        missResult.Columns.ShouldBe(emptyMatch.Columns);
        missCrm.UpstreamCalls.ShouldBe(0);
        existsCrm.UpstreamCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Miss_CountStar_ReturnsOneRowWithZero_LikeAnEmptyMatch()
    {
        var (crm, pipeline) = Setup();

        var rows = await pipeline.RunAsync("SELECT COUNT(*) AS n" + From + "WHERE c.id = '2'");

        Convert.ToInt64(rows.ShouldHaveSingleItem()["n"]).ShouldBe(0);
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("SELECT o.id, c.id AS cid FROM erp.public.orders o LEFT JOIN crm_api.public.customers c ON c.id = o.customer_id WHERE c.id = '2'")]
    [InlineData("SELECT COUNT(*) AS n FROM crm_api.public.customers c WHERE c.id = '2'")]
    [InlineData("SELECT c.id FROM crm_api.public.customers c WHERE c.id IN ('2', '4')")]
    [InlineData("SELECT o.id FROM erp.public.orders o LEFT JOIN crm_api.public.customers c ON c.id = o.customer_id AND c.id = '2' ORDER BY o.id")]
    public async Task Miss_ResultEqualsFullFetchResult_Differential(string sql)
    {
        var (shortCrm, shortCircuit) = Setup();
        var (fullCrm, full) = Setup(options: new VirtualFilterFederationOptions { PushdownEnabled = false });

        var withShortCircuit = await shortCircuit.RunAsync(sql);
        var withFullFetch = await full.RunAsync(sql);

        Normalize(withShortCircuit).ShouldBe(Normalize(withFullFetch));
        fullCrm.UpstreamCalls.ShouldBe(1);
        shortCrm.UpstreamCalls.ShouldBeLessThanOrEqualTo(1);
    }

    private static List<string> Normalize(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        rows.Select(r => string.Join("|", r.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Miss_AllQueriedKeysUnauthorized_InList_SkipsUpstream()
    {
        var (crm, pipeline) = Setup();

        (await Ids(pipeline, "SELECT c.id" + From + "WHERE c.id IN ('2', '9')")).ShouldBeEmpty();
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Hit_Adaptive_NoCapability_FullFetchEnforced()
    {
        var (crm, pipeline) = Setup();

        (await Ids(pipeline, "SELECT c.id" + From + "WHERE c.id IN ('1', '2')")).ShouldBe(["1"]);
        crm.UpstreamCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("SELECT c.id" + From + "WHERE o.id = '99'")]                                           // another table's column
    [InlineData("SELECT c.id" + From + "WHERE c.id = '2' OR c.id = '1'")]                              // not conjunctive
    [InlineData("SELECT c.id" + From + "WHERE c.id NOT IN ('1')")]                                    // not a lookup
    [InlineData("SELECT c.id" + From + "WHERE c.name = 'Beta'")]                                      // not a key column
    public async Task NoPointLookup_FullFetch_OnlyAuthorizedRows(string sql)
    {
        var (crm, pipeline) = Setup();

        var ids = await Ids(pipeline, sql);

        ids.ShouldBeSubsetOf(["1", "3"]);
        crm.UpstreamCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("SELECT id FROM crm_api.public.customers WHERE id = '2' UNION ALL SELECT id FROM erp.public.orders")]
    [InlineData("WITH c AS (SELECT id FROM crm_api.public.customers WHERE id = '2') SELECT c.id FROM c UNION ALL SELECT id FROM erp.public.orders")]
    [InlineData("SELECT o.id FROM erp.public.orders o WHERE o.customer_id IN (SELECT id FROM crm_api.public.customers WHERE id = '2') OR o.id = '10'")]
    public async Task IneligibleShapes_AreNeverShortCircuited(string sql)
    {
        // The UNION/CTE/sub-query would otherwise lose the SQL side of the result.
        var (crm, pipeline) = Setup();

        (await Ids(pipeline, sql)).ShouldNotBeNull();
        crm.UpstreamCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Union_WithTheSqlSide_StillReturnsTheSqlRows()
    {
        var (_, pipeline) = Setup();

        (await Ids(pipeline, "SELECT id FROM crm_api.public.customers WHERE id = '2' UNION ALL SELECT id FROM erp.public.orders"))
            .ShouldBe(["10", "11", "12"]);
    }

    [Fact]
    public async Task PushdownDisabled_NeverShortCircuits()
    {
        var (crm, pipeline) = Setup(options: new VirtualFilterFederationOptions { PushdownEnabled = false });

        (await Ids(pipeline, "SELECT c.id" + From + "WHERE c.id = '2'")).ShouldBeEmpty();
        crm.UpstreamCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ShortCircuitOnly_Miss_ZeroRows_ConnectorNeverCalled()
    {
        var (crm, pipeline) = Setup(VirtualFilterExecutionStrategy.ShortCircuitOnly);

        (await pipeline.RunAsync("SELECT c.id" + From + "WHERE c.id = '2'")).ShouldBeEmpty();
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ShortCircuitOnly_HitWithoutCapability_DeniedPushdownUnavailable()
    {
        var (crm, pipeline) = Setup(VirtualFilterExecutionStrategy.ShortCircuitOnly);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => pipeline.RunAsync("SELECT c.id" + From + "WHERE c.id = '1'"));

        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_UNAVAILABLE");
        crm.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task PushdownOnly_Miss_ZeroRows_ConnectorNeverCalled()
    {
        var (crm, pipeline) = Setup(VirtualFilterExecutionStrategy.PushdownOnly);

        (await pipeline.RunAsync("SELECT c.id" + From + "WHERE c.id = '2'")).ShouldBeEmpty();
        crm.UpstreamCalls.ShouldBe(0);
    }

    // ----- planner level: composite keys, typed literals, tuple cap -----

    private static (IReadOnlyList<ResolvedSourceTable> Tables, TrinoSqlEngine.Ast.Nodes.SqlStatement Statement) Parse(string sql, TableMetadata table)
    {
        var metadata = Engine.Analyze(sql.AsMemory());
        var tables = metadata.ReferencedTables.Select(t => new ResolvedSourceTable(t, table.Identifier, table, table.Identifier.Domain)).ToList();
        var (tree, _) = Engine.Parse(sql.AsMemory());
        return (tables, new SqlAstBuilder().BuildStatement(tree));
    }

    private static async Task<VirtualFilterTablePlan> PlanAsync(
        string sql, TableMetadata table, AppliedVirtualFilterBinding pair, FakeKeySetProvider keys, VirtualFilterFederationOptions? options = null)
    {
        var (tables, statement) = Parse(sql, table);
        var decision = Unrestricted(table).WithMandatoryPredicate("EXISTS (SELECT 1 FROM vf)", [pair.Filter.Name], [pair]);
        var planner = new VirtualFilterFederationPlanner(keys, Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { Federation = options ?? new() } }));
        var plans = await planner.PlanAsync(statement, tables, new Dictionary<TableIdentifier, TableAccessDecision> { [table.Identifier] = decision }, Tenant, CancellationToken.None);
        return plans.ShouldHaveSingleItem().Value;
    }

    private static AppliedVirtualFilterBinding CompositePair() => new(
        KeyFilter("vf_composite", "client_id", "tenant_code"),
        new FilterBinding
        {
            FilterName = "vf_composite",
            TargetPattern = "crm_api.public.customers",
            ColumnMap = new Dictionary<string, string> { ["client_id"] = "id", ["tenant_code"] = "account_id" }
        },
        1);

    [Theory]
    [InlineData("c.id = '101' AND c.account_id = 'B'", true)]    // component mismatch: miss
    [InlineData("c.id = '999' AND c.account_id = 'A'", true)]    // component mismatch: miss
    [InlineData("c.id = '101' AND c.account_id = 'A'", false)]   // hit
    [InlineData("c.id = '101'", false)]                           // one component only: no decision, scan
    public async Task CompositePointLookup_MissOnOneComponent_IsTier1Miss(string where, bool miss)
    {
        var keys = new FakeKeySetProvider().With("vf_composite", ["101", "A"], ["102", "B"]);

        var plan = await PlanAsync($"SELECT c.id FROM crm_api.public.customers c WHERE {where}", Customers("varchar"), CompositePair(), keys);

        plan.SkipUpstream.ShouldBe(miss);
        plan.Tier.ShouldBe(miss ? VirtualFilterTier.PointLookup : VirtualFilterTier.FullFetch);
    }

    [Theory]
    [InlineData("c.id = 47", false)]       // integer literal on an integer column, authorized
    [InlineData("c.id = 48", true)]
    [InlineData("c.id = '47'", false)]     // strict integer text converts
    [InlineData("c.id = 'abc'", false)]    // not convertible: scan, never a miss
    [InlineData("c.id = 47.5", false)]     // not an integer: scan
    public async Task IntegerColumn_LiteralsAreCanonicalized_NonConvertibleIsAScan(string where, bool miss)
    {
        var keys = new FakeKeySetProvider().With("vf_customers", [47L]);

        var plan = await PlanAsync($"SELECT c.id FROM crm_api.public.customers c WHERE {where}", Customers("bigint"), PairById(), keys);

        plan.SkipUpstream.ShouldBe(miss);
    }

    [Fact]
    public async Task TextColumn_NumericLiteral_IsNotAPointLookup()
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["47"]);

        var plan = await PlanAsync("SELECT c.id FROM crm_api.public.customers c WHERE c.id = 48", Customers("varchar"), PairById(), keys);

        plan.SkipUpstream.ShouldBeFalse();   // a text column is only matched by text literals
    }

    [Fact]
    public async Task CartesianProductAboveTheCap_IsAScan()
    {
        var keys = new FakeKeySetProvider().With("vf_composite", ["101", "A"]);
        var sql = "SELECT c.id FROM crm_api.public.customers c WHERE c.id IN ('1', '2', '3') AND c.account_id IN ('X', 'Y')";

        var capped = await PlanAsync(sql, Customers("varchar"), CompositePair(), keys, new VirtualFilterFederationOptions { MaxPointLookupTuples = 5 });
        var allowed = await PlanAsync(sql, Customers("varchar"), CompositePair(), keys, new VirtualFilterFederationOptions { MaxPointLookupTuples = 6 });

        capped.SkipUpstream.ShouldBeFalse();
        allowed.SkipUpstream.ShouldBeTrue();
    }

    [Fact]
    public async Task EmptyKeySet_StaysTier0_EvenForAPointLookup()
    {
        var plan = await PlanAsync("SELECT c.id FROM crm_api.public.customers c WHERE c.id = '1'", Customers("varchar"), PairById(), new FakeKeySetProvider());

        plan.Tier.ShouldBe(VirtualFilterTier.EmptyKeySet);
    }
}
