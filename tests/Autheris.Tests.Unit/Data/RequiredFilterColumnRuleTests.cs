namespace Autheris.Tests.Unit.Data;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.OData;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using RequiredFilterColumnMissingException = TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException;

/// <summary>MISC-03/MISC-04: the shared required-filter-column rule matches whole identifiers only.</summary>
[Trait("Category", "Security")]
public sealed class RequiredFilterColumnRuleTests
{
    private static readonly TableIdentifier Id = new("sales", "public", "orders");

    private static TableMetadata Meta(string[] required, string[][]? groups = null, string? tenantColumn = null)
    {
        var columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "int" }, new() { ColumnName = "region", DataType = "varchar" } };
        if (tenantColumn != null)
        {
            columns.Add(new TableColumn { ColumnName = tenantColumn, DataType = "varchar" });
        }

        return new TableMetadata
        {
            Identifier = Id,
            Table = new Table
            {
                SourceName = "sales",
                SchemaName = "public",
                TableName = "orders",
                RequiredFilterColumns = [.. required],
                RequiredFilterColumnGroups = [.. (groups ?? []).Select(g => System.Collections.Immutable.ImmutableArray.Create(g))]
            },
            Columns = columns
        };
    }

    private static TableAccessDecision Decision(string? rowFilter) =>
        TableAccessDecision.Allowed(Id, new Dictionary<string, ColumnAccessLevel>(), rowFilter, hasUnconstrainedColumnAllow: true);

    private static bool Satisfied(TableMetadata meta, string? rowFilter, bool tenantCounts = true) =>
        RequiredFilterColumnRule.IsSatisfied(meta, Decision(rowFilter), clientConstrainedColumns: null, tenantCounts);

    [Fact]
    [Trait("Category", "Security")]
    public void LongerIdentifier_region_code_DoesNotSatisfy_region() =>
        Satisfied(Meta(["region"]), "region_code = 'EU'").ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void StringLiteralContainingColumn_DoesNotSatisfy() =>
        Satisfied(Meta(["region"]), "status = 'region'").ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void TenantIdColumn_DoesNotSatisfy_id() =>
        Satisfied(Meta(["id"], tenantColumn: "tenant_id"), "tenant_id = 'a'").ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void CommentContainingColumn_DoesNotSatisfy()
    {
        Satisfied(Meta(["region"]), "status = 1 -- region\n").ShouldBeFalse();
        Satisfied(Meta(["region"]), "status = 1 /* region */").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void UnparseableRowFilter_FailsClosed()
    {
        Satisfied(Meta(["region"]), "status = 'unterminated region").ShouldBeFalse();
        Satisfied(Meta(["region"]), "   ").ShouldBeFalse();
        Satisfied(Meta(["region"]), null).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WholeIdentifier_Satisfies()
    {
        Satisfied(Meta(["region"]), "region = 'EU'").ShouldBeTrue();
        Satisfied(Meta(["region"]), "(\"REGION\" = 'EU')").ShouldBeTrue();
        Satisfied(Meta(["region"]), "t.region IN ('EU')").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TenantColumn_SatisfiesOnlyWhenCounted()
    {
        var meta = Meta(["tenant_id"], tenantColumn: "tenant_id");
        Satisfied(meta, null, tenantCounts: true).ShouldBeTrue();
        Satisfied(meta, null, tenantCounts: false).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GroupSatisfied_WhenAllMembersSatisfied()
    {
        var meta = Meta([], [["region", "id"]]);
        Satisfied(meta, "region = 'EU'").ShouldBeFalse();
        Satisfied(meta, "region = 'EU' AND id = 1").ShouldBeTrue();
    }

    [Fact]
    public void ClientFilterColumns_Satisfy()
    {
        RequiredFilterColumnRule.IsSatisfied(Meta(["region"]), Decision(null), ["REGION"], tenantColumnCounts: true).ShouldBeTrue();
    }

    [Fact]
    public void NoRequirement_IsSatisfied() =>
        Satisfied(Meta([]), null).ShouldBeTrue();

    [Fact]
    [Trait("Category", "Security")]
    public void NoSubstringMatchingOnRowFilterTextRemainsInSource()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var offenders = System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(dir!.FullName, "src"), "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(System.IO.File.ReadAllText(f), @"CombinedRowFilterSql\s*\.Contains\("))
            .ToList();
        offenders.ShouldBeEmpty();
    }

    private static GovernedTableReadPlanner Planner => new();

    private static TableReadIntent IntentWithOData(string odataFilter) =>
        new(Id, ["id"], ODataFilterParser.Parse(odataFilter, DatabaseDialect.PostgreSql), null, 10, 0, "OData");

    private static TableReadIntent IntentWithTree(TreeFilter treeFilter)
    {
        var meta = Meta(["region"]);
        var clause = TreeFilterClauseBuilder.Build(treeFilter, name =>
            meta.GetColumn(name) is { } col ? new ColumnRef(col.ColumnName, ColumnValueKind.String) : null);
        return new(Id, ["id"], clause, null, 10, 0, "GraphQL");
    }

    private static TableAccessDecision ClearDecision() => TableAccessDecision.Allowed(
        Id,
        new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["region"] = ColumnAccessLevel.Clear },
        hasUnconstrainedColumnAllow: true);

    [Fact]
    [Trait("Category", "Security")]
    public void Neq_Does_Not_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("region ne 'EU'"), meta, dec));
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithTree(new TreeComparison("region", TreeFilterOperator.Neq, "EU")), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void IsNull_Does_Not_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("region eq null"), meta, dec));
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithTree(new TreeComparison("region", TreeFilterOperator.IsNull, true)), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Like_Percent_Does_Not_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("contains(region, '%')"), meta, dec));
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithTree(new TreeComparison("region", TreeFilterOperator.Contains, "%")), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NotIn_Does_Not_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.Throw<RequiredFilterColumnMissingException>(() =>
            Planner.Plan(IntentWithTree(new TreeComparison("region", TreeFilterOperator.NotIn, new List<object?> { "EU" })), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Or_With_Unconstrained_Branch_Does_Not_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("region eq 'EU' or id eq 1"), meta, dec));
        Should.Throw<RequiredFilterColumnMissingException>(() =>
            Planner.Plan(IntentWithTree(new TreeOrFilter([
                new TreeComparison("region", TreeFilterOperator.Eq, "EU"),
                new TreeComparison("id", TreeFilterOperator.Eq, "1")
            ])), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Not_Eq_Does_Not_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("not (region eq 'EU')"), meta, dec));
        Should.Throw<RequiredFilterColumnMissingException>(() =>
            Planner.Plan(IntentWithTree(new TreeNotFilter(new TreeComparison("region", TreeFilterOperator.Eq, "EU"))), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Eq_In_Range_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.NotThrow(() => Planner.Plan(IntentWithOData("region eq 'EU'"), meta, dec));
        Should.NotThrow(() => Planner.Plan(IntentWithOData("region ge 'A'"), meta, dec));
        Should.NotThrow(() => Planner.Plan(IntentWithTree(new TreeComparison("region", TreeFilterOperator.In, new List<object?> { "EU", "US" })), meta, dec));
        Should.NotThrow(() => Planner.Plan(IntentWithTree(new TreeComparison("region", TreeFilterOperator.Gt, "A")), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Or_Constrained_On_Both_Branches_Satisfies()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.NotThrow(() => Planner.Plan(IntentWithOData("region eq 'EU' or region eq 'US'"), meta, dec));
        Should.NotThrow(() => Planner.Plan(IntentWithTree(new TreeOrFilter([
            new TreeComparison("region", TreeFilterOperator.Eq, "EU"),
            new TreeComparison("region", TreeFilterOperator.Eq, "US")
        ])), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Null_ConstrainedColumns_Fails_Closed()
    {
        var meta = Meta(["region"]);
        var dec = Decision(null);
        RequiredFilterColumnRule.IsSatisfied(meta, dec, null, tenantColumnCounts: true).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ColumnToColumn_And_NullComparison_Do_Not_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("region eq id"), meta, dec));
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("region eq null"), meta, dec));
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("null eq region"), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FunctionWrappedColumn_Does_Not_Satisfy()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        Should.Throw<RequiredFilterColumnMissingException>(() => Planner.Plan(IntentWithOData("tolower(region) eq 'eu'"), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Between_And_NonEmptyIn_Satisfy()
    {
        var cols = ImmutableArray.Create(
            new CatalogColumn("id", "integer"),
            new CatalogColumn("region", "varchar"));
        var catalog = new InMemoryTableCatalog([
            new TableCatalogEntry(new TableIdentity("dbo", "orders"), cols, null, ImmutableArray.Create("region"), 1)
        ], "dbo");
        var engine = new FastSqlEngine();
        var req = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new NoPolicyProvider(),
                Masks = new NoMaskProvider(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };

        engine.Compile("SELECT id FROM orders WHERE region BETWEEN 'A' AND 'Z'".AsMemory(), req, default).ShouldNotBeNull();
        engine.Compile("SELECT id FROM orders WHERE region IN ('EU', 'US')".AsMemory(), req, default).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void New_Filter_Producer_Without_ConstrainedColumns_Fails_Closed()
    {
        var meta = Meta(["region"]);
        var dec = ClearDecision();
        var producerWithoutConstrained = new TableFilterClause("\"region\" = 'EU'", new Dictionary<string, object?>(), ["region"])
        {
            TrinoSqlFactory = _ => "\"region\" = 'EU'"
        };
        producerWithoutConstrained.ConstrainedColumns.ShouldBeNull();

        Should.Throw<RequiredFilterColumnMissingException>(() =>
            Planner.Plan(new TableReadIntent(Id, ["id"], producerWithoutConstrained, null, 10, 0, "Rest"), meta, dec));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dml_Where_Like_On_Required_Column_Is_Rejected()
    {
        var cols = ImmutableArray.Create(
            new CatalogColumn("id", "integer"),
            new CatalogColumn("region", "varchar"));
        var catalog = new InMemoryTableCatalog([
            new TableCatalogEntry(new TableIdentity("dbo", "orders"), cols, null, ImmutableArray.Create("region"), 1)
        ], "dbo");
        var engine = new FastSqlEngine();
        var req = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = StatementPermissions.Update | StatementPermissions.Delete,
            Policy = new GovernancePolicy
            {
                RowFilters = new NoPolicyProvider(),
                Masks = new NoMaskProvider(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
                Write = new DmlWritePermit(
                    DmlWriteOperation.Update,
                    new HashSet<string>(["id", "region"], StringComparer.OrdinalIgnoreCase),
                    FrozenSet<string>.Empty)
            }
        };

        Should.Throw<RequiredFilterColumnMissingException>(() =>
            engine.Compile("UPDATE dbo.orders SET region = 'US' WHERE region LIKE '%'".AsMemory(), req, default));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Error_Does_Not_Name_Column()
    {
        var meta = Meta(["secret_partition_col"]);
        var dec = ClearDecision();

        var ex = Should.Throw<RequiredFilterColumnMissingException>(() =>
            Planner.Plan(new TableReadIntent(Id, ["id"], null, null, 10, 0, "Rest"), meta, dec));

        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing));
        ex.Message.ShouldNotContain("secret_partition_col");
    }

    private sealed class NoPolicyProvider : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
    }

    private sealed class NoMaskProvider : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new KeyNotFoundException();
    }
}
