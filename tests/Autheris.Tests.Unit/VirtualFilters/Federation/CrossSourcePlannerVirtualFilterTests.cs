namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-6 (ST-27): the cross-source planner carries the Virtual Filter plan on the staging request, never
/// rewrites the user query for Virtual Filters, and denies instead of guessing a staging table (no <c>s0</c> fallback).
/// </summary>
public sealed class CrossSourcePlannerVirtualFilterTests
{
    private static readonly FastSqlEngine Engine = new();
    private static readonly TableMetadata Crm = VirtualFilterEnforcementFixture.Customers("varchar");
    private static readonly TableMetadata Erp = SqlTable("erp", "public", "orders", "id", "customer_id");

    private static (SqlQueryMetadata Metadata, IReadOnlyList<ResolvedSourceTable> Tables, Dictionary<TableIdentifier, TableAccessDecision> Decisions) Setup(string sql)
    {
        var metadata = Engine.Analyze(sql.AsMemory());
        var pair = VirtualFilterEnforcementFixture.PairById();
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [Crm.Identifier] = Unrestricted(Crm).WithMandatoryPredicate("p", ["vf_customers"], [pair]),
            [Erp.Identifier] = Unrestricted(Erp)
        };
        var tables = metadata.ReferencedTables
            .Select(t => t.TableName == "customers"
                ? new ResolvedSourceTable(t, Crm.Identifier, Crm, "crm_api")
                : new ResolvedSourceTable(t, Erp.Identifier, Erp, "erp"))
            .ToList();
        return (metadata, tables, decisions);
    }

    private static VirtualFilterTablePlan PlanFor(TableMetadata table, VirtualFilterTier tier = VirtualFilterTier.FullFetch)
    {
        var pair = VirtualFilterEnforcementFixture.PairById();
        var keys = VirtualFilterEnforcementFixture.Keys([VirtualFilterKeyType.Text], ["1"]);
        return new VirtualFilterTablePlan(VirtualFilterEnforcementFixture.Enforcement(table, (pair, keys)), tier, tier == VirtualFilterTier.EmptyKeySet, "test");
    }

    private static CrossSourcePlan Plan(string sql, IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>? plans)
    {
        var (metadata, tables, decisions) = Setup(sql);
        return new CrossSourcePlanner(Engine).Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, plans);
    }

    private const string Join = "SELECT c.id, o.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id";

    [Fact]
    public void Plan_VirtualFilterPlan_IsAttachedToTheRequestOfItsTableOnly_AndTheQueryIsNotRewritten()
    {
        var plan = Plan(Join, new Dictionary<TableIdentifier, VirtualFilterTablePlan> { [Crm.Identifier] = PlanFor(Crm) });

        plan.StagingRequests.Single(r => r.Metadata.Identifier == Crm.Identifier).VirtualFilterPlan.ShouldNotBeNull();
        plan.StagingRequests.Single(r => r.Metadata.Identifier == Erp.Identifier).VirtualFilterPlan.ShouldBeNull();
        plan.StagingRequests.Count.ShouldBe(2);
        plan.StagingNames.Count.ShouldBe(2);
        plan.GeneratedDuckDbSql.ShouldNotContain("virtual_filter");
        plan.GeneratedDuckDbSql.ShouldNotContain("INNER JOIN s2");
    }

    [Fact]
    public void Plan_VirtualFilterPlanForATableNotInTheQuery_IsDenied()
    {
        var other = Crm with { Identifier = new TableIdentifier("crm_api", "public", "invoices") };

        var ex = Should.Throw<GatewaySecurityException>(() =>
            Plan(Join, new Dictionary<TableIdentifier, VirtualFilterTablePlan> { [other.Identifier] = PlanFor(other) }));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void Plan_SelfJoin_AttachesThePlanToEveryOccurrence()
    {
        const string sql = "SELECT a.id, b.id FROM crm_api.public.customers a JOIN crm_api.public.customers b ON a.id = b.id";
        var plan = Plan(sql, new Dictionary<TableIdentifier, VirtualFilterTablePlan> { [Crm.Identifier] = PlanFor(Crm) });

        plan.StagingRequests.Count.ShouldBe(2);
        plan.StagingRequests.ShouldAllBe(r => r.VirtualFilterPlan != null);
    }

    [Fact]
    public void Plan_TableNameThatIsNeitherResolvedNorACte_IsDenied_NotMappedToS0()
    {
        // ST-27: the rewriter used to map any unknown table name to s0 (the first staged table).
        const string sql = "SELECT c.id FROM crm_api.public.customers c JOIN unknown_src.public.ghost g ON g.id = c.id";
        var metadata = Engine.Analyze(sql.AsMemory());
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision> { [Crm.Identifier] = Unrestricted(Crm) };
        var onlyCrm = new List<ResolvedSourceTable> { new(metadata.ReferencedTables[0], Crm.Identifier, Crm, "crm_api") };

        Should.Throw<System.Exception>(() =>
            new CrossSourcePlanner(Engine).Plan(sql, metadata, onlyCrm, decisions, new CrossSourceOptions { Enabled = true }));
    }

    [Fact]
    public void Plan_CteName_IsKeptAndItsBodyReadsTheStagedTable()
    {
        const string sql = "WITH recent AS (SELECT id, customer_id FROM erp.public.orders) " +
                           "SELECT c.id FROM crm_api.public.customers c JOIN recent r ON r.customer_id = c.id";
        var plan = Plan(sql, null);

        plan.GeneratedDuckDbSql.ShouldContain("recent");
        plan.GeneratedDuckDbSql.ShouldContain("s0");
        plan.GeneratedDuckDbSql.ShouldContain("s1");
    }

    [Fact]
    public void Plan_PreParsedStatement_IsUsedInsteadOfParsingAgain()
    {
        var (metadata, tables, decisions) = Setup(Join);
        var (tree, _) = Engine.Parse(Join.AsMemory());
        var statement = new SqlAstBuilder().BuildStatement(tree);

        // The raw SQL is deliberately different (and invalid for these tables); the supplied statement wins.
        var plan = new CrossSourcePlanner(Engine).Plan(
            "SELECT 1", metadata, tables, decisions, new CrossSourceOptions { Enabled = true }, null, statement);

        plan.GeneratedDuckDbSql.ShouldContain("s0");
        plan.GeneratedDuckDbSql.ShouldContain("s1");
    }
}
