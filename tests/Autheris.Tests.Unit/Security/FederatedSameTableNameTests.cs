namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Federation.Optimization;
using Autheris.Application.Sql.Services;
using TrinoSqlEngine.Ast.Nodes;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>
/// PARITY-GAP-017 / PARITY-GAP-018 reproduction (WP-F, SEC-PG-16): two sources register the same
/// <c>main.parity_sales</c> table. A federated join must apply each source's decision (column masks, row policy,
/// required filters) to its own table, give the staged tables distinct names and rows, and keep column predicates and
/// projections attributed to the right source. Mirrors the parity host catalog (SQL tables, columns with a TableId,
/// <c>SourceName</c> = source) in process.
/// </summary>
public sealed class FederatedSameTableNameTests
{
    private static readonly FastSqlEngine Engine = new();

    private const string JoinHead =
        "FROM default.main.parity_sales a JOIN second.main.parity_sales b ON a.id = b.id";

    private static TableMetadata Sales(string source, Dictionary<string, MaskingRule>? masks = null)
    {
        var id = Guid.NewGuid();
        string[] cols = ["id", "region", "product", "qty", "amount"];
        return new TableMetadata
        {
            Identifier = new TableIdentifier(source, "main", "parity_sales"),
            Table = new Table
            {
                Id = id,
                TableName = "parity_sales",
                SchemaName = "main",
                SourceType = "Sqlite",
                SourceName = source,
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns = cols.Select(c => new TableColumn { TableId = id, ColumnName = c, DataType = c is "id" or "qty" ? "integer" : "varchar" }).ToList(),
            ColumnMaskingRules = masks ?? new Dictionary<string, MaskingRule>()
        };
    }

    private sealed record Setup(
        SqlQueryMetadata Metadata,
        ResolvedSourceTable[] Tables,
        Dictionary<TableIdentifier, TableAccessDecision> Decisions,
        TableMetadata DefaultMeta,
        TableMetadata SecondMeta);

    private static Setup Build(
        string sql,
        Dictionary<string, ColumnAccessLevel>? defaultAccess = null,
        Dictionary<string, ColumnAccessLevel>? secondAccess = null,
        Dictionary<string, MaskingRule>? defaultMasks = null,
        Dictionary<string, MaskingRule>? secondMasks = null,
        string? defaultRowFilter = null,
        string? secondRowFilter = null)
    {
        var metadata = Engine.Analyze(sql.AsMemory());
        var dm = Sales("default", defaultMasks);
        var sm = Sales("second", secondMasks);

        // Resolve every referenced table to the metadata of its own catalog, as the router does.
        var tables = metadata.ReferencedTables
            .Select(t => string.Equals(t.Catalog, "second", StringComparison.OrdinalIgnoreCase)
                ? new ResolvedSourceTable(t, sm.Identifier, sm, "second")
                : new ResolvedSourceTable(t, dm.Identifier, dm, "default"))
            .ToArray();

        TableAccessDecision Decision(TableMetadata m, Dictionary<string, ColumnAccessLevel>? access, string? rowFilter) =>
            TableAccessDecision.Allowed(m.Identifier, access ?? new Dictionary<string, ColumnAccessLevel>(), rowFilter,
                hasUnconstrainedColumnAllow: access == null);

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [dm.Identifier] = Decision(dm, defaultAccess, defaultRowFilter),
            [sm.Identifier] = Decision(sm, secondAccess, secondRowFilter)
        };

        return new Setup(metadata, tables, decisions, dm, sm);
    }

    private static CrossSourcePlan Plan(Setup s, string sql) =>
        new CrossSourcePlanner(Engine).Plan(
            sql, s.Metadata, s.Tables, s.Decisions, new CrossSourceOptions { Enabled = true },
            PlannerTestIdentity.User, PlannerTestIdentity.Tenant);

    public static TheoryData<string> Predicates => new()
    {
        "SELECT a.product, b.amount " + JoinHead + " WHERE a.region = 'North'",
        "SELECT a.product, b.amount " + JoinHead + " WHERE a.id = 1",
        "SELECT a.product, b.amount " + JoinHead + " WHERE b.qty > 1",
        "SELECT a.product, b.amount " + JoinHead + " WHERE a.id IN (1, 2)",
        "SELECT a.product, b.amount " + JoinHead + " WHERE a.region = 'North' AND b.qty > 1",
        "SELECT a.product, b.amount " + JoinHead + " WHERE 1 = 1"
    };

    [Theory]
    [Trait("Category", "Security")]
    [MemberData(nameof(Predicates))]
    public void Plan_SEC_PG_16_ColumnPredicate_OnSameSchemaTableInTwoSources_IsPlanned_NotRejected(string sql)
    {
        // PARITY-GAP-017: every column predicate was rejected as 403 SQL_POLICY_VIOLATION.
        var s = Build(sql);

        var plan = Plan(s, sql);

        plan.StagingRequests.Count.ShouldBe(2);
        plan.StagingNames.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Plan_SEC_PG_16_StagingRequests_KeepOwnSourceMetadataDecisionAndProjection()
    {
        // PARITY-GAP-018: projected columns of both sides must reach the staging projections of their own table.
        var sql = "SELECT a.product, a.qty, b.amount " + JoinHead + " WHERE a.region = 'North' ORDER BY a.id";
        var s = Build(sql);

        var plan = Plan(s, sql);

        plan.StagingRequests.Count.ShouldBe(2);
        var def = plan.StagingRequests.Single(r => r.Metadata.Identifier == s.DefaultMeta.Identifier);
        var sec = plan.StagingRequests.Single(r => r.Metadata.Identifier == s.SecondMeta.Identifier);

        def.StagingName.ShouldNotBe(sec.StagingName);
        def.Decision.ShouldBeSameAs(s.Decisions[s.DefaultMeta.Identifier]);
        sec.Decision.ShouldBeSameAs(s.Decisions[s.SecondMeta.Identifier]);

        def.Projection.ShouldContain("id");
        def.Projection.ShouldContain("product");
        def.Projection.ShouldContain("qty");
        def.Projection.ShouldContain("region");
        def.Projection.ShouldNotContain("amount");

        sec.Projection.ShouldContain("id");
        sec.Projection.ShouldContain("amount");
        sec.Projection.ShouldNotContain("product");
        sec.Projection.ShouldNotContain("region");

        // The DuckDB query reads each alias from its own staging table.
        plan.GeneratedDuckDbSql.ShouldContain($"\"{def.StagingName}\" AS \"a\"");
        plan.GeneratedDuckDbSql.ShouldContain($"\"{sec.StagingName}\" AS \"b\"");
        plan.GeneratedDuckDbSql.ShouldNotContain("parity_sales");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("SELECT a.product, COUNT(*) n, SUM(b.amount) total " + JoinHead + " GROUP BY a.product")]
    [InlineData("SELECT b.amount, a.qty, a.region " + JoinHead + " ORDER BY a.id")]
    public void Plan_SEC_PG_16_Gap018Queries_ProjectEachSidesColumnsFromItsOwnTable(string sql)
    {
        // PARITY-GAP-018: the staged s1 (second) table carried the projection of s0 (default): "b.amount" was never read.
        var s = Build(sql);

        var plan = Plan(s, sql);

        var def = plan.StagingRequests.Single(r => r.Metadata.Identifier == s.DefaultMeta.Identifier);
        var sec = plan.StagingRequests.Single(r => r.Metadata.Identifier == s.SecondMeta.Identifier);
        sec.Projection.ShouldContain("amount");
        sec.Projection.ShouldContain("id");
        def.Projection.ShouldNotContain("amount");
        def.Projection.ShouldContain("id");
        def.Projection.ShouldContain(sql.Contains("GROUP BY") ? "product" : "qty");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Plan_SEC_PG_16_PushedPredicateParameters_AreAcceptedByTheSqlReadOfTheirOwnSource()
    {
        // PARITY-GAP-017: the pushed column predicate of a SQL source carries "@p_pd_<n>" parameters (PushedClauseBuilder). The staging read
        // passes them as request parameters of the governed SQL read (SqlTableReadExecutor -> GovernedSqlExecutor), whose reserved-name
        // check rejects the "p_pd_" prefix with WebSqlPolicyException -> HTTP 403 SQL_POLICY_VIOLATION.
        var sql = "SELECT a.product, b.amount " + JoinHead + " WHERE a.region = 'North'";
        var s = Build(sql);

        var plan = Plan(s, sql);

        var def = plan.StagingRequests.Single(r => r.Metadata.Identifier == s.DefaultMeta.Identifier);
        def.PushdownFilter.ShouldNotBeNull();
        def.PushdownFilter!.Parameters.Count.ShouldBeGreaterThan(0);
        plan.StagingRequests.Single(r => r.Metadata.Identifier == s.SecondMeta.Identifier).PushdownFilter.ShouldBeNull();

        Should.NotThrow(() => GovernedSqlExecutionService.EnsureClientParameterNames(def.PushdownFilter.Parameters, plannedTableRead: true));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Plan_SEC_PG_16_Mask_OfOneSource_DoesNotLeakToTheSameNamedTableOfTheOtherSource()
    {
        // "amount" is masked only in source "second": filtering on a.amount (default, clear) is allowed,
        // filtering on b.amount (second, masked) is rejected (SEC-FILTER-01) - attribution is per source.
        var second = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["amount"] = ColumnAccessLevel.Mask };
        var secondMasks = new Dictionary<string, MaskingRule> { ["amount"] = new() { RuleType = "REDACT" } };

        var allowedSql = "SELECT a.product, b.id " + JoinHead + " WHERE a.amount = '5'";
        Should.NotThrow(() => Plan(Build(allowedSql, secondAccess: second, secondMasks: secondMasks), allowedSql));

        var deniedSql = "SELECT a.product, b.id " + JoinHead + " WHERE b.amount = '5'";
        Should.Throw<Exception>(() => Plan(Build(deniedSql, secondAccess: second, secondMasks: secondMasks), deniedSql));

        // And the mirror image: masked only in "default".
        var first = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["amount"] = ColumnAccessLevel.Mask };
        var firstMasks = new Dictionary<string, MaskingRule> { ["amount"] = new() { RuleType = "REDACT" } };
        var mirrorAllowed = "SELECT a.product, b.id " + JoinHead + " WHERE b.amount = '5'";
        Should.NotThrow(() => Plan(Build(mirrorAllowed, defaultAccess: first, defaultMasks: firstMasks), mirrorAllowed));
        var mirrorDenied = "SELECT a.product, b.id " + JoinHead + " WHERE a.amount = '5'";
        Should.Throw<Exception>(() => Plan(Build(mirrorDenied, defaultAccess: first, defaultMasks: firstMasks), mirrorDenied));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Plan_SEC_PG_16_RequiredFilter_OfOneSource_IsNotSatisfiedByTheOtherSourcesColumn()
    {
        // Only "default" requires a filter on "region". The decision of "second" must not satisfy it.
        var sql = "SELECT a.product, b.amount " + JoinHead;
        var s = Build(sql);
        var requiring = s.DefaultMeta with { Table = s.DefaultMeta.Table with { RequiredFilterColumns = ["region"] } };
        var tables = s.Tables.Select(t => t.Metadata.Identifier == requiring.Identifier ? t with { Metadata = requiring } : t).ToArray();

        Should.Throw<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(() =>
            new CrossSourcePlanner(Engine).Plan(sql, s.Metadata, tables, s.Decisions, new CrossSourceOptions { Enabled = true },
                PlannerTestIdentity.User, PlannerTestIdentity.Tenant));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task StageAsync_SEC_PG_16_TwoSourcesSameTableName_StageOwnRowsOwnMasksUnderDistinctNames()
    {
        var sql = "SELECT a.product, a.amount AS a_amount, b.amount AS b_amount " + JoinHead + " WHERE a.region = 'North'";
        var second = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["amount"] = ColumnAccessLevel.Mask
        };
        var secondMasks = new Dictionary<string, MaskingRule> { ["amount"] = new() { RuleType = "REDACT" } };
        var s = Build(sql, secondAccess: second, secondMasks: secondMasks);
        var plan = Plan(s, sql);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        maskingProvider.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");

        void Wire(TableMetadata meta, string source, object?[] amounts)
        {
            var connector = Substitute.For<IAutherisConnector>();
            connector.ConnectorId.Returns(source);
            var recordSource = Substitute.For<IConnectorRecordSource>();
            connector.RecordSource.Returns(recordSource);
            connector.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
                .Returns([ConnectorSplit.Default(source + "-split")]);
            var rows = amounts.Select((a, i) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = i + 1,
                ["region"] = "North",
                ["product"] = source + "-product",
                ["amount"] = a
            }).ToList();
            recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
                .Returns(rows);
            registry.TryGetConnectorForTable(meta.Identifier, out Arg.Any<IAutherisConnector>()!)
                .Returns(x => { x[1] = connector; return true; });
        }

        Wire(s.DefaultMeta, "default", ["10.00", "20.00"]);
        Wire(s.SecondMeta, "second", ["99.00", "98.00", "97.00"]);

        var service = new FederatedStagingService(
            registry, maskingProvider, Substitute.For<IAuditLogRepository>(),
            Options.Create(new GatewayOptions()), NullLogger<FederatedStagingService>.Instance);

        var budget = new FederationBudget(5, 1000, 5000, 32 * 1024 * 1024, 128 * 1024 * 1024, 30, 4);
        var staged = await service.StageAsync(plan.StagingRequests, PlannerTestIdentity.User, PlannerTestIdentity.Tenant, budget);

        staged.Count.ShouldBe(2);
        staged.Select(x => x.StagingTableName).Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(2);

        var def = staged.Single(x => x.Table == s.DefaultMeta.Identifier);
        var sec = staged.Single(x => x.Table == s.SecondMeta.Identifier);

        def.GovernedRows.Count.ShouldBe(2);
        def.GovernedRows.ShouldAllBe(r => (string?)r["product"] == "default-product");
        def.GovernedRows.Select(r => r["id"]).ShouldBe([1, 2]);

        // PARITY-GAP-018: the non-key columns of both sides are present, not NULL.
        sec.GovernedRows.Count.ShouldBe(3);
        sec.GovernedRows.ShouldAllBe(r => r.ContainsKey("amount") && r["amount"] != null);

        // The mask of "second" applies to its own rows only, never to the same-named table of "default".
        sec.GovernedRows.ShouldAllBe(r => (string?)r["amount"] == "***");
        def.GovernedRows.Select(r => (string?)r["amount"]).ShouldBe(["10.00", "20.00"]);
    }

    private static (bool Rejected, CrossSourcePlan? Plan) TryPlan(Setup s, string sql)
    {
        try
        {
            return (false, Plan(s, sql));
        }
        catch (Exception e) when (e is Autheris.Domain.Exceptions.GatewaySecurityException or Autheris.Application.Sql.WebSqlPolicyException)
        {
            return (true, null);
        }
    }

    private const string AggSql = "SELECT a.product, SUM(b.amount) total " + JoinHead + " GROUP BY a.product HAVING SUM(b.amount) > 1 ORDER BY SUM(b.amount)";

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedProjection_AggregateOnlyColumn_IsStagedNotNull()
    {
        var plan = Plan(Build(AggSql), AggSql);

        plan.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "second").Projection.ShouldContain("amount");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedProjection_DenyColumnInAggregate_FailsClosedNotNull()
    {
        var deny = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["amount"] = ColumnAccessLevel.Deny };
        var s = Build(AggSql, secondAccess: deny);

        var (rejected, plan) = TryPlan(s, AggSql);

        // Either the planner rejects, or the Deny column is not staged (the final query then fails unresolved); it is never staged or NULL-filled.
        if (!rejected)
        {
            plan!.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "second").Projection.ShouldNotContain("amount");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedProjection_MaskedColumnInAggregate_UsesMaskedValues()
    {
        var mask = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["amount"] = ColumnAccessLevel.Mask };
        var masks = new Dictionary<string, MaskingRule> { ["amount"] = new() { RuleType = "REDACT" } };
        var s = Build(AggSql, secondAccess: mask, secondMasks: masks);

        var (rejected, plan) = TryPlan(s, AggSql);

        // The masked column is staged only through the governed read (its decision keeps the Mask level); it is never staged raw.
        if (!rejected)
        {
            var sec = plan!.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "second");
            sec.Projection.ShouldContain("amount");
            sec.Decision.GetEffectiveColumnAccess("amount", sec.Metadata).ShouldBe(ColumnAccessLevel.Mask);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedProjection_UnqualifiedColumn_StagedOnAllowedTablesOnly()
    {
        var sql = "SELECT a.product, SUM(qty) " + JoinHead + " GROUP BY a.product";
        var deny = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["qty"] = ColumnAccessLevel.Deny };
        var s = Build(sql, secondAccess: deny);

        var plan = Plan(s, sql);

        plan.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "default").Projection.ShouldContain("qty");
        plan.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "second").Projection.ShouldNotContain("qty");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedProjection_AmbiguousUnqualified_FailsInFinalQuery()
    {
        // The planner stages the unqualified column on both tables; it does not pick one source. The ambiguity is then raised by the final
        // DuckDB query (both staged tables carry "qty"), never resolved silently.
        var sql = "SELECT a.product, SUM(qty) " + JoinHead + " GROUP BY a.product";
        var plan = Plan(Build(sql), sql);

        plan.StagingRequests.ShouldAllBe(r => r.Projection.Contains("qty"));
        plan.StagingNames.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedProjection_UnknownQualifier_FailsUnresolved()
    {
        var sql = "SELECT a.product, SUM(zz.amount) " + JoinHead + " GROUP BY a.product";

        var (rejected, plan) = TryPlan(Build(sql), sql);

        // A qualifier that names no staged table stages nothing: the amount column is not staged for any source.
        if (!rejected)
        {
            plan!.StagingRequests.ShouldAllBe(r => !r.Projection.Contains("amount"));
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedProjection_UnclassifiableNode_FailsClosedOrStagesAllAllowed()
    {
        var weird = new SelectStatement(null,
            new QuerySpecification(false, [new ColumnSelectItem(new UnknownExpression(), null)], null, null, null, null), null, null);

        var references = StatementColumnReferences.Collect(weird);

        references.Unclassifiable.ShouldBeTrue();
        references.Columns.ShouldBeEmpty();
    }

    private sealed record UnknownExpression : Expression;

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedProjection_SelectStar_ExcludesDeny()
    {
        var sql = "SELECT * " + JoinHead;
        var deny = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["amount"] = ColumnAccessLevel.Deny };
        var plan = Plan(Build(sql, secondAccess: deny), sql);

        var sec = plan.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "second");
        sec.Projection.ShouldNotContain("amount");
        sec.Projection.ShouldContain("id");
        plan.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "default").Projection.ShouldContain("amount");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedPushedPredicate_StillAppliesRowPolicyAndRequiredFilter()
    {
        var sql = "SELECT a.product, b.amount " + JoinHead + " WHERE a.region = 'North'";
        var s = Build(sql, defaultRowFilter: "tenant_id = 'T1'", secondRowFilter: "tenant_id = 'T2'");

        var plan = Plan(s, sql);

        var def = plan.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "default");
        var sec = plan.StagingRequests.Single(r => r.Metadata.Identifier.Domain == "second");
        def.PushdownFilter.ShouldNotBeNull();
        def.Decision.CombinedRowFilterSql.ShouldBe("tenant_id = 'T1'");
        sec.Decision.CombinedRowFilterSql.ShouldBe("tenant_id = 'T2'");

        // The required-filter rule of the other test stays enforced when a pushed predicate exists.
        var requiring = s.DefaultMeta with { Table = s.DefaultMeta.Table with { RequiredFilterColumns = ["product"] } };
        var tables = s.Tables.Select(t => t.Metadata.Identifier == requiring.Identifier ? t with { Metadata = requiring } : t).ToArray();
        Should.Throw<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(() =>
            new CrossSourcePlanner(Engine).Plan(sql, s.Metadata, tables, s.Decisions, new CrossSourceOptions { Enabled = true },
                PlannerTestIdentity.User, PlannerTestIdentity.Tenant));
    }

    private static void PlanWithRequiredRegion(string whereClause)
    {
        var sql = "SELECT a.product, b.amount " + JoinHead + " WHERE " + whereClause;
        var s = Build(sql);
        var requiring = s.DefaultMeta with { Table = s.DefaultMeta.Table with { RequiredFilterColumns = ["region"] } };
        var tables = s.Tables.Select(t => t.Metadata.Identifier == requiring.Identifier ? t with { Metadata = requiring } : t).ToArray();
        new CrossSourcePlanner(Engine).Plan(sql, s.Metadata, tables, s.Decisions, new CrossSourceOptions { Enabled = true },
            PlannerTestIdentity.User, PlannerTestIdentity.Tenant);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("a.region = a.region")]
    [InlineData("a.region IS NOT NULL OR 1 = 1")]
    [InlineData("a.region = 'x' OR a.qty = 2")]
    [InlineData("a.region <> 'x'")]
    [InlineData("NOT (a.region = 'x')")]
    [InlineData("lower(a.region) = 'x'")]
    [InlineData("b.region = 'North'")]
    [InlineData("a.qty = 1")]
    public void FederatedRequiredFilter_WeakOrUnrelatedPredicate_DoesNotSatisfy_REQUIRED_FILTER_COLUMN_MISSING(string where) =>
        Should.Throw<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(() => PlanWithRequiredRegion(where));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("a.region = 'North'")]
    [InlineData("a.region IN ('North', 'South')")]
    [InlineData("a.region = 'North' AND b.qty > 1")]
    [InlineData("a.region = 'x' OR a.region = 'y'")]
    public void FederatedRequiredFilter_StrictConstrainingPredicate_Satisfies(string where) =>
        Should.NotThrow(() => PlanWithRequiredRegion(where));

    [Fact]
    [Trait("Category", "Security")]
    public async Task StageAsync_RequiredFilterColumnGroup_IsEnforcedOnTheFederatedPath()
    {
        // The planner only checks RequiredFilterColumns; the column groups are enforced in StageAsync (before any read).
        var meta = Sales("default");
        meta = meta with { Table = meta.Table with { RequiredFilterColumnGroups = [["region"], ["product", "qty"]] } };
        var request = (Microsoft.Extensions.Options.IOptions<GatewayOptions>)Options.Create(new GatewayOptions());
        var service = new FederatedStagingService(
            Substitute.For<IAutherisConnectorRegistry>(), Substitute.For<IColumnMaskingProvider>(), Substitute.For<IAuditLogRepository>(),
            request, NullLogger<FederatedStagingService>.Instance);
        var budget = new FederationBudget(5, 1000, 5000, 32 * 1024 * 1024, 128 * 1024 * 1024, 30, 4);

        StagingTableRequest Req(TableFilterClause? filter) => new(
            new TableAccessTarget(null, "main", "parity_sales", "parity_sales", "main.parity_sales"), meta,
            TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            "s0", ["id"], filter);

        // no filter, and a filter that only mentions the column (not constraining) -> rejected before any read
        await Should.ThrowAsync<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(() =>
            service.StageAsync([Req(null)], PlannerTestIdentity.User, PlannerTestIdentity.Tenant, budget));
        await Should.ThrowAsync<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(() =>
            service.StageAsync([Req(new TableFilterClause("region = region", new Dictionary<string, object?>(), ["region"]))],
                PlannerTestIdentity.User, PlannerTestIdentity.Tenant, budget));
    }
}
