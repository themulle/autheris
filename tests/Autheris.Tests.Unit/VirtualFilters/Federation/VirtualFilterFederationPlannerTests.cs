namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using Autheris.Tests.Common;
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
using Shouldly;
using TrinoSqlEngine;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.VirtualFilterEnforcementFixture;

/// <summary>
/// F-GOV-14 WI-6 (M5, ST-10 key provider half, ST-22, ST-37): the federation planner builds one plan per protected HTTP
/// table. Every plan carries an enforcement; Tier 0 only skips the upstream call; every invalid or unsupported
/// combination denies with a stable code before any key is loaded.
/// </summary>
public sealed class VirtualFilterFederationPlannerTests
{
    private static readonly FastSqlEngine Engine = new();
    private static readonly TableMetadata Customers = HttpTable("crm_api", "public", "customers", "id", "name");

    private static ResolvedSourceTable Resolved(TableMetadata table)
    {
        var sql = $"SELECT * FROM {table.Identifier.Domain}.{table.Identifier.Schema}.{table.Identifier.TableName}";
        var metadata = Engine.Analyze(sql.AsMemory());
        return new ResolvedSourceTable(metadata.ReferencedTables[0], table.Identifier, table, table.Identifier.Domain);
    }

    private static AppliedVirtualFilterBinding Pair(
        string filterName = "vf_customers",
        string keyColumn = "client_id",
        string targetColumn = "id",
        VirtualFilterExecutionStrategy strategy = VirtualFilterExecutionStrategy.Adaptive,
        VirtualFilter? filter = null) =>
        new(filter ?? KeyFilter(filterName, keyColumn),
            new FilterBinding
            {
                FilterName = filterName,
                TargetPattern = "crm_api.public.customers",
                ColumnMap = new Dictionary<string, string> { [keyColumn] = targetColumn },
                Strategy = strategy
            },
            1);

    private static TableAccessDecision Protected(TableMetadata table, params AppliedVirtualFilterBinding[] pairs) =>
        Unrestricted(table).WithMandatoryPredicate("EXISTS (SELECT 1 FROM vf)", pairs.Select(p => p.Filter.Name).Distinct().ToList(), pairs);

    private static Task<IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>> PlanAsync(
        FakeKeySetProvider keys,
        IReadOnlyList<ResolvedSourceTable> tables,
        params (TableMetadata Table, TableAccessDecision Decision)[] decisions) =>
        new VirtualFilterFederationPlanner(keys).PlanAsync(
            null,
            tables,
            decisions.ToDictionary(d => d.Table.Identifier, d => d.Decision),
            Tenant,
            CancellationToken.None);

    private static Task<IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>> PlanOneAsync(
        FakeKeySetProvider keys, TableMetadata table, TableAccessDecision decision) =>
        PlanAsync(keys, [Resolved(table)], (table, decision));

    [Fact]
    public async Task Plan_AppliedBinding_IsTier3FullFetchWithEnforcement()
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"], ["3"]);

        var plans = await PlanOneAsync(keys, Customers, Protected(Customers, Pair()));

        var plan = plans.ShouldHaveSingleItem().Value;
        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
        plan.SkipUpstream.ShouldBeFalse();
        plan.Enforcement.ShouldNotBeNull();
        plan.Enforcement.Table.ShouldBe(Customers.Identifier);
        plan.Table.ShouldBe(Customers.Identifier);
        plan.Enforcement.Bindings.ShouldHaveSingleItem().Filter.Name.ShouldBe("vf_customers");
        plan.Enforcement.Apply([Row(("id", "1")), Row(("id", "2")), Row(("id", "3"))]).Select(r => r["id"]).ShouldBe(["1", "3"]);
        keys.Calls.ShouldHaveSingleItem().Tenant.ShouldBe(Tenant);
        keys.Calls[0].KeyTypes.ShouldBe([VirtualFilterKeyType.Text]);
    }

    [Fact]
    public async Task Plan_EmptyKeySet_IsTier0SkipUpstreamButStillCarriesEnforcement()
    {
        var keys = new FakeKeySetProvider();

        var plan = (await PlanOneAsync(keys, Customers, Protected(Customers, Pair()))).ShouldHaveSingleItem().Value;

        plan.Tier.ShouldBe(VirtualFilterTier.EmptyKeySet);
        plan.SkipUpstream.ShouldBeTrue();
        plan.Enforcement.ShouldNotBeNull();
        plan.Enforcement.Apply([Row(("id", "1"))]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(VirtualFilterExecutionStrategy.Adaptive)]
    [InlineData(VirtualFilterExecutionStrategy.DuckDbHashJoin)]
    [InlineData(VirtualFilterExecutionStrategy.ShortCircuitOnly)]
    [InlineData(VirtualFilterExecutionStrategy.PushdownOnly)]
    public async Task Plan_EmptyKeySet_SkipsUpstreamForEveryStrategy(VirtualFilterExecutionStrategy strategy)
    {
        var plan = (await PlanOneAsync(new FakeKeySetProvider(), Customers, Protected(Customers, Pair(strategy: strategy)))).Single().Value;

        plan.Tier.ShouldBe(VirtualFilterTier.EmptyKeySet);
        plan.SkipUpstream.ShouldBeTrue();
    }

    [Theory]
    [InlineData(VirtualFilterExecutionStrategy.Adaptive)]
    [InlineData(VirtualFilterExecutionStrategy.DuckDbHashJoin)]
    public async Task Plan_NonPushdownStrategies_AreFullFetch(VirtualFilterExecutionStrategy strategy)
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var plan = (await PlanOneAsync(keys, Customers, Protected(Customers, Pair(strategy: strategy)))).Single().Value;

        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
    }

    [Fact]
    public async Task Plan_ShortCircuitOnlyOnScan_IsDeniedScanNotPermitted()
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanOneAsync(keys, Customers, Protected(Customers, Pair(strategy: VirtualFilterExecutionStrategy.ShortCircuitOnly))));

        ex.ErrorCode.ShouldBe("VF_SCAN_NOT_PERMITTED");
    }

    [Fact]
    public async Task Plan_PushdownOnly_IsDeniedPushdownUnavailable()
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanOneAsync(keys, Customers, Protected(Customers, Pair(strategy: VirtualFilterExecutionStrategy.PushdownOnly))));

        ex.ErrorCode.ShouldBe("VF_PUSHDOWN_UNAVAILABLE");
    }

    [Fact]
    public async Task Plan_TwoPairs_MostRestrictiveStrategyWinsAndAnyEmptySetSkipsUpstream()
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);   // vf_other has no keys
        var decision = Protected(Customers, Pair(), Pair("vf_other", "other_id", "id"));

        var plan = (await PlanOneAsync(keys, Customers, decision)).Single().Value;

        plan.Tier.ShouldBe(VirtualFilterTier.EmptyKeySet);
        plan.SkipUpstream.ShouldBeTrue();
        plan.Enforcement.Bindings.Count.ShouldBe(2);
        keys.CalledFilters.OrderBy(n => n).ShouldBe(["vf_customers", "vf_other"]);
    }

    [Fact]
    public async Task Plan_TwoPairs_EnforcementRequiresBothSets()
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"], ["2"]).With("vf_other", ["2"], ["3"]);

        var plan = (await PlanOneAsync(keys, Customers, Protected(Customers, Pair(), Pair("vf_other", "other_id", "id")))).Single().Value;

        plan.Tier.ShouldBe(VirtualFilterTier.FullFetch);
        plan.Enforcement.Apply([Row(("id", "1")), Row(("id", "2")), Row(("id", "3"))]).Select(r => r["id"]).ShouldBe(["2"]);
    }

    [Fact]
    public async Task Plan_TableWithoutVirtualFilter_HasNoPlanAndLoadsNoKeys()
    {
        var keys = new FakeKeySetProvider();

        var plans = await PlanOneAsync(keys, Customers, Unrestricted(Customers, "name = 'x'"));

        plans.ShouldBeEmpty();
        keys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_SqlTableWithVirtualFilter_HasNoPlan_ItsPredicateRunsInTheSqlPath()
    {
        var erp = SqlTable("crm_api", "public", "customers", "id", "name");
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var plans = await PlanOneAsync(keys, erp, Protected(erp, Pair()));

        plans.ShouldBeEmpty();
        keys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_SelfJoinedTable_IsPlannedAndLoadedOnce()
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var plans = await PlanAsync(keys, [Resolved(Customers), Resolved(Customers)], (Customers, Protected(Customers, Pair())));

        plans.Count.ShouldBe(1);
        keys.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Plan_PredicateWithoutBindings_IsDeniedEnforcementUnavailable_NoKeysLoaded()
    {
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);
        var decision = Unrestricted(Customers).WithMandatoryPredicate("EXISTS (SELECT 1 FROM vf)", ["vf_customers"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanOneAsync(keys, Customers, decision));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_UNAVAILABLE");
        keys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_MissingDecision_IsDeniedEnforcementIncomplete()
    {
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(new FakeKeySetProvider(), [Resolved(Customers)]));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Theory]
    [InlineData("id; DROP")]
    [InlineData("no_such_column")]
    public async Task Plan_ColumnMapTargetInvalidOrMissing_IsDeniedBindingMisconfigured_NoKeysLoaded(string target)
    {
        // ST-37: a profile written straight to the repository bypasses write-time validation.
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanOneAsync(keys, Customers, Protected(Customers, Pair(targetColumn: target))));

        ex.ErrorCode.ShouldBe("VF_BINDING_MISCONFIGURED");
        keys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_UnsupportedKeyColumnType_IsDeniedKeyTypeMismatch_NoKeysLoaded()
    {
        var table = Customers with
        {
            Columns = Customers.Columns.Select(c => c.ColumnName == "id" ? new TableColumn { ColumnName = "id", DataType = "date" } : c).ToList()
        };
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanOneAsync(keys, table, Protected(table, Pair())));

        ex.ErrorCode.ShouldBe("VF_KEY_TYPE_MISMATCH");
        keys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_SqlDefinitionFilter_IsDeniedKeySourceUnsupported_NoKeysLoaded()
    {
        var sqlFilter = KeyFilter("vf_customers", "client_id") with { Structured = null, Sql = "SELECT client_id FROM erp.auth.client" };
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            PlanOneAsync(keys, Customers, Protected(Customers, Pair(filter: sqlFilter))));

        ex.ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
        keys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_SecondTableInvalid_DeniesBeforeAnyKeyOfTheFirstTableIsLoaded()
    {
        var orders = HttpTable("erp_api", "public", "orders", "id");
        var ordersPair = new AppliedVirtualFilterBinding(
            KeyFilter("vf_orders", "order_id"),
            new FilterBinding { FilterName = "vf_orders", TargetPattern = "erp_api.public.orders", ColumnMap = new Dictionary<string, string> { ["order_id"] = "missing" } },
            1);
        var keys = new FakeKeySetProvider().With("vf_customers", ["1"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAsync(
            keys,
            [Resolved(Customers), Resolved(orders)],
            (Customers, Protected(Customers, Pair())),
            (orders, Protected(orders, ordersPair))));

        ex.ErrorCode.ShouldBe("VF_BINDING_MISCONFIGURED");
        keys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_KeyProviderFails_IsDeniedSourceUnavailable_WithoutLeakingTheInnerMessage()
    {
        var keys = new FakeKeySetProvider { Failure = new InvalidOperationException("secret value 4711 in message") };

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanOneAsync(keys, Customers, Protected(Customers, Pair())));

        ex.ErrorCode.ShouldBe("VIRTUAL_FILTER_SOURCE_UNAVAILABLE");
        ex.Message.ShouldNotContain("4711");
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task Plan_KeyProviderDenies_KeepsItsCode()
    {
        var keys = new FakeKeySetProvider { Failure = new GatewaySecurityException("too large", VirtualFilterErrorCodes.KeySetTooLarge) };

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanOneAsync(keys, Customers, Protected(Customers, Pair())));

        ex.ErrorCode.ShouldBe("VF_KEY_SET_TOO_LARGE");
    }

    [Fact]
    public async Task Plan_KeyLoadingTimesOut_IsDeniedSourceUnavailable_EvenIfTheProviderIgnoresCancellation()
    {
        // ST-23 (planner half, SEC-12): the request timeout bounds key loading.
        var keys = new FakeKeySetProvider { Delay = TimeSpan.FromSeconds(60), IgnoreCancellation = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        // The provider ignores cancellation and would block for 60 s: the planner only returns (inside the hang guard) if it enforces the timeout itself.

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            new VirtualFilterFederationPlanner(keys).PlanAsync(
                null,
                [Resolved(Customers)],
                new Dictionary<TableIdentifier, TableAccessDecision> { [Customers.Identifier] = Protected(Customers, Pair()) },
                Tenant,
                cts.Token)).WaitAsync(WaitHelper.HangGuard);

        ex.ErrorCode.ShouldBe("VIRTUAL_FILTER_SOURCE_UNAVAILABLE");
    }

    [Fact]
    public async Task Plan_OnlyTheDecidedBindingsAreLoaded_ST10()
    {
        // ST-10: the decision of the caller carries only the caller's binding; no other filter key is ever loaded.
        var keys = new FakeKeySetProvider().With("vf_alice", ["1"]).With("vf_bob", ["2"]);

        await PlanOneAsync(keys, Customers, Protected(Customers, Pair("vf_alice")));

        keys.CalledFilters.ShouldBe(["vf_alice"]);
    }
}
