namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Audit;
using Autheris.Domain.Connectors;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.Common.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Autheris.Application.Sql;
using TrinoSqlEngine.Ast.Emit;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>SEC-SIMPL-94: every member of a pushed-down subtree is Clear under its own decision; all others are staged on their own.</summary>
[Trait("Category", "Security")]
public sealed class FederatedSubtreeColumnDecisionTests
{
    private static readonly FastSqlEngine Engine = new();
    private static readonly IReadOnlyDictionary<string, ColumnAccessLevel> NoEntries = new Dictionary<string, ColumnAccessLevel>();

    private sealed record Scenario(
        string Sql,
        SqlQueryMetadata Analysis,
        List<ResolvedSourceTable> Tables,
        Dictionary<TableIdentifier, TableAccessDecision> Decisions);

    private static TableMetadata Meta(string domain, string name, Dictionary<string, MaskingRule>? rules = null, params string[] cols) => new()
    {
        Identifier = new TableIdentifier(domain, "public", name),
        Table = new Table { TableName = name, SchemaName = "public", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql, SourceName = domain, IsActive = true },
        Columns = cols.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
        ColumnMaskingRules = rules ?? new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
    };

    private static TableAccessDecision Open(TableMetadata m) => TableAccessDecision.Allowed(m.Identifier, NoEntries, hasUnconstrainedColumnAllow: true);

    private static TableAccessDecision Constrained(TableMetadata m, ColumnAccessLevel level) =>
        TableAccessDecision.Allowed(m.Identifier, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["secret"] = level }, hasUnconstrainedColumnAllow: false);

    private static ResolvedSourceTable Resolve(TableMetadata m, TableAccessTarget? target = null) =>
        new(target ?? default, m.Identifier, m, m.Identifier.Domain);

    private static Scenario Build(string sql, params (TableMetadata Meta, TableAccessDecision Decision)[] members)
    {
        var analysis = Engine.Analyze(sql.AsMemory());
        var tables = members.Select(m => Resolve(m.Meta, analysis.ReferencedTables.First(r => string.Equals(r.TableName, m.Meta.Identifier.TableName, StringComparison.OrdinalIgnoreCase)))).ToList();
        return new Scenario(sql, analysis, tables, members.ToDictionary(m => m.Meta.Identifier, m => m.Decision));
    }

    private static CrossSourcePlan Plan(Scenario s) =>
        new CrossSourcePlanner(Engine).Plan(s.Sql, s.Analysis, s.Tables, s.Decisions, new CrossSourceOptions { Enabled = true }, PlannerTestIdentity.User, PlannerTestIdentity.Tenant);

    private static IEnumerable<TableIdentifier> Pushed(CrossSourcePlan p) =>
        p.StagingRequests.Where(r => r.Subtree != null).SelectMany(r => r.Subtree!.MemberTables).Select(t => t.Metadata.Identifier);

    [Fact]
    public void FederatedSubtree_IsPushdownEligible_DefaultsToFalse_ForMissingDecisionEmptyCatalogOrUnknownLevel()
    {
        var m = Meta("pg", "orders", null, "id", "secret");
        var t = Resolve(m);
        SubtreeCompiler.IsPushdownEligible(t, Open(m)).ShouldBeTrue();
        SubtreeCompiler.IsPushdownEligible(t, null).ShouldBeFalse();
        SubtreeCompiler.IsPushdownEligible(null, Open(m)).ShouldBeFalse();
        SubtreeCompiler.IsPushdownEligible(t, TableAccessDecision.Denied(m.Identifier, "x")).ShouldBeFalse();
        SubtreeCompiler.IsPushdownEligible(t, Constrained(m, (ColumnAccessLevel)99)).ShouldBeFalse();

        var noColumns = Meta("pg", "orders", null);
        var withEntry = TableAccessDecision.Allowed(noColumns.Identifier, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, hasUnconstrainedColumnAllow: true);
        SubtreeCompiler.IsPushdownEligible(Resolve(noColumns), withEntry).ShouldBeFalse();
    }

    [Fact]
    public void FederatedSubtree_CatalogMaskingRuleForColumnAbsentFromColumnList_NotEligible()
    {
        var m = Meta("pg", "orders", new Dictionary<string, MaskingRule> { ["ghost"] = new MaskingRule { RuleType = "REDACT" } }, "id");
        SubtreeCompiler.IsPushdownEligible(Resolve(m), Open(m)).ShouldBeFalse();
    }

    [Fact]
    public void FederatedSubtree_ScopeRestrictedClearance_NotEligible()
    {
        var m = Meta("pg", "orders", null, "id");
        var restricted = Open(m) with
        {
            ScopeClearance = new RestrictedScopeClearance(new HashSet<string> { "pii" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        SubtreeCompiler.IsPushdownEligible(Resolve(m), restricted).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ColumnAccessLevel.Deny, true)]
    [InlineData(ColumnAccessLevel.Deny, false)]
    [InlineData(ColumnAccessLevel.Mask, true)]
    [InlineData(ColumnAccessLevel.Mask, false)]
    public void FederatedSubtree_NonRootMemberConstrainedColumn_IsNotPushedDown_BothJoinOrders(ColumnAccessLevel level, bool memberFirst)
    {
        var root = Meta("pg", "orders", null, "id");
        var member = Meta("pg", "items", null, "id", "secret");
        var sql = memberFirst
            ? "SELECT i.id FROM pg.public.items i JOIN pg.public.orders o ON o.id = i.id JOIN ms.public.ship s ON s.id = o.id"
            : "SELECT o.id FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id JOIN ms.public.ship s ON s.id = o.id";
        var ship = Meta("ms", "ship", null, "id");
        var plan = Plan(Build(sql, (root, Open(root)), (member, Constrained(member, level)), (ship, Open(ship))));

        Pushed(plan).ShouldNotContain(member.Identifier);
        plan.StagingRequests.Any(r => r.Subtree == null && r.Metadata.Identifier == member.Identifier).ShouldBeTrue();
    }

    [Fact]
    public void FederatedSubtree_ColumnConstrainedMember_FallsBackToSingleTableStaging()
    {
        var a = Meta("pg", "orders", null, "id");
        var b = Meta("pg", "items", null, "id", "secret");
        var plan = Plan(Build("SELECT o.id FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id", (a, Open(a)), (b, Constrained(b, ColumnAccessLevel.Mask))));
        Pushed(plan).ShouldBeEmpty();
        plan.StagingRequests.Count(r => r.Subtree == null).ShouldBe(2);
    }

    [Fact]
    public void FederatedSubtree_AllMembersClear_StillPushesDown()
    {
        var a = Meta("pg", "orders", null, "id");
        var b = Meta("pg", "items", null, "id");
        var plan = Plan(Build("SELECT o.id FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id", (a, Open(a)), (b, Open(b))));
        Pushed(plan).Count().ShouldBe(2);
    }

    [Fact]
    public void FederatedSubtree_Partitioner_IneligibleTable_IsRemainingSingle()
    {
        var a = Meta("pg", "orders", null, "id");
        var b = Meta("pg", "items", null, "id", "secret");
        var s = Build("SELECT o.id FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id", (a, Open(a)), (b, Constrained(b, ColumnAccessLevel.Deny)));
        var (tree, _) = Engine.Parse(s.Sql.AsMemory());
        var statement = new SqlAstBuilder().BuildStatement(tree);

        SubtreeGraphPartitioner.Partition(statement, s.Tables, 5).Subtrees.Count.ShouldBe(1);
        var result = SubtreeGraphPartitioner.Partition(statement, s.Tables, 5, t => SubtreeCompiler.IsPushdownEligible(t, s.Decisions[t.Metadata.Identifier]));
        result.Subtrees.ShouldBeEmpty();
        result.RemainingSingleTables.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FederatedSubtree_Compile_RejectsIneligibleRoot(bool ineligibleIsRoot)
    {
        var a = Meta("pg", "orders", null, "id", "secret");
        var b = Meta("pg", "items", null, "id");
        var (rootMeta, otherMeta) = ineligibleIsRoot ? (a, b) : (b, a);
        var s = Build("SELECT o.id FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id", (a, ineligibleIsRoot ? Constrained(a, ColumnAccessLevel.Mask) : Open(a)), (b, ineligibleIsRoot ? Open(b) : Constrained(b, ColumnAccessLevel.Mask)));
        var (tree, _) = Engine.Parse(s.Sql.AsMemory());
        var subtree = SubtreeGraphPartitioner.Partition(new SqlAstBuilder().BuildStatement(tree), s.Tables, 5).Subtrees.Single();

        var ex = Should.Throw<GatewaySecurityException>(() => SubtreeCompiler.Compile(
            subtree, s.Decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, PlannerTestIdentity.Tenant));
        ex.Message.ShouldNotContain("secret");
        rootMeta.ShouldNotBeNull();
        otherMeta.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("SELECT o.id FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id WHERE i.secret = 'x'")]
    [InlineData("SELECT o.id FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.secret")]
    public void FederatedSubtree_FallbackSingleTable_FilterOnMaskedMemberColumn_StillRefused(string sql)
    {
        var a = Meta("pg", "orders", null, "id");
        var b = Meta("pg", "items", new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["secret"] = new MaskingRule { RuleType = "REDACT" } }, "id", "secret");
        var ex = Should.Throw<WebSqlPolicyException>(() => Plan(Build(sql, (a, Open(a)), (b, Constrained(b, ColumnAccessLevel.Mask)))));
        ex.Code.ShouldBe(GovernedSqlErrorCodes.FilterOnMaskedColumn);
    }

    private static TableMetadata Wide(string domain, string name, params (string Column, string Rule)[] rules) =>
        Meta(domain, name, rules.ToDictionary(r => r.Column, r => new MaskingRule { RuleType = r.Rule }, StringComparer.OrdinalIgnoreCase), "id", "secret", "ssn");

    private static TableAccessDecision Levels(TableMetadata m, ColumnAccessLevel secret, ColumnAccessLevel ssn) =>
        TableAccessDecision.Allowed(m.Identifier, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["secret"] = secret, ["ssn"] = ssn }, hasUnconstrainedColumnAllow: false);

    /// <summary>Plans the query and stages every single-table request through the real staging service; the fake connector returns raw, all-column rows.</summary>
    private static async Task<Dictionary<TableIdentifier, OlapTableSource>> StageAsync(Scenario s)
    {
        var plan = Plan(s);
        var connector = Substitute.For<IAutherisConnector>();
        var splits = Substitute.For<IConnectorSplitManager>();
        var source = Substitute.For<IConnectorRecordSource>();
        connector.SplitManager.Returns(splits);
        connector.RecordSource.Returns(source);
        splits.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>()).Returns([ConnectorSplit.Default("s")]);
        source.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => [new Dictionary<string, object?> { ["id"] = 1, ["secret"] = "raw-secret", ["ssn"] = "raw-ssn" }]);
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(Arg.Any<TableIdentifier>(), out Arg.Any<IAutherisConnector>()!).Returns(x => { x[1] = connector; return true; });
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns(x => "M:" + x.ArgAt<MaskingRule>(2).RuleType);

        var staging = new FederatedStagingService(registry, masking, audit, Options.Create(new GatewayOptions()), NullLogger<FederatedStagingService>.Instance);
        var staged = await staging.StageAsync(plan.StagingRequests.Where(r => r.Subtree == null).ToList(), PlannerTestIdentity.User, PlannerTestIdentity.Tenant,
            new FederationBudget(5, 1000, 5000, 32 * 1024 * 1024, 128 * 1024 * 1024, 30, 4));
        return staged.ToDictionary(t => t.Table);
    }

    private static IReadOnlyDictionary<string, object?> OnlyRow(OlapTableSource t) => t.GovernedRows.Single();

    internal static async Task AssertDenyColumnNeverStagedAsync(bool wildcard)
    {
        var root = Wide("pg", "orders");
        var member = Wide("pg", "items");
        var sql = wildcard
            ? "SELECT * FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id"
            : "SELECT o.id, i.id AS iid FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id";
        var staged = await StageAsync(Build(sql, (root, Open(root)), (member, Levels(member, ColumnAccessLevel.Deny, ColumnAccessLevel.Clear))));

        var row = OnlyRow(staged[member.Identifier]);
        row.ContainsKey("secret").ShouldBeFalse();
        row.Values.ShouldNotContain("raw-secret");
        row.ContainsKey("id").ShouldBeTrue();
    }

    internal static async Task AssertMemberMaskAppliedPerMemberAsync(bool nameClash)
    {
        var root = Wide("pg", "orders", ("ssn", "REDACT"));
        var member = nameClash ? Wide("pg", "items", ("ssn", "NULLIFY")) : Wide("pg", "items", ("secret", "NULLIFY"));
        var rootDecision = nameClash ? TableAccessDecision.Allowed(root.Identifier, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["secret"] = ColumnAccessLevel.Clear, ["ssn"] = ColumnAccessLevel.Clear }, hasUnconstrainedColumnAllow: true) : Levels(root, ColumnAccessLevel.Clear, ColumnAccessLevel.Mask);
        var memberDecision = nameClash ? Levels(member, ColumnAccessLevel.Clear, ColumnAccessLevel.Mask) : Levels(member, ColumnAccessLevel.Mask, ColumnAccessLevel.Clear);
        var staged = await StageAsync(Build("SELECT * FROM pg.public.orders o JOIN pg.public.items i ON o.id = i.id", (root, rootDecision), (member, memberDecision)));

        var memberRow = OnlyRow(staged[member.Identifier]);
        memberRow[nameClash ? "ssn" : "secret"].ShouldBe("M:NULLIFY");
        if (!nameClash)
        {
            OnlyRow(staged[root.Identifier])["ssn"].ShouldBe("M:REDACT");
        }
    }

    [Fact]
    public async Task FederatedSubtree_NonRootMemberDenyColumn_IsNeverStaged_WildcardAndExplicit()
    {
        await AssertDenyColumnNeverStagedAsync(wildcard: true);
        await AssertDenyColumnNeverStagedAsync(wildcard: false);
    }

    [Fact]
    public Task FederatedSubtree_NameClashRootClearMemberMasked_StaysMasked() => AssertMemberMaskAppliedPerMemberAsync(nameClash: true);

    [Fact]
    public Task FederatedSubtree_MemberMask_IsAppliedWithTheMembersOwnRule() => AssertMemberMaskAppliedPerMemberAsync(nameClash: false);
}
