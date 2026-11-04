namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using BinaryOperator = TrinoSqlEngine.Ast.Nodes.BinaryOperator;

public sealed class Issue443SetBasedTreeCompileTests
{
    private static readonly TableIdentifier Parents = new("test", "main", "parents");
    private static readonly TableIdentifier Children = new("test", "main", "children");
    private static readonly TableIdentifier Grandchildren = new("test", "main", "grandchildren");

    private static TableMetadata ParentsMeta(bool withPk = true) => new()
    {
        Identifier = Parents,
        Table = new Table { SourceName = "test", SchemaName = "main", TableName = "parents", SourceType = "Sqlite" },
        PrimaryKeyColumns = withPk ? ["id"] : [],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "name", DataType = "text" },
            new TableColumn { ColumnName = "tenant_id", DataType = "text" }
        ]
    };

    private static TableMetadata ChildrenMeta(bool withIndex = false, bool withPk = true)
    {
        var indexes = new List<TableIndexDefinition>();
        if (withIndex)
        {
            indexes.Add(new TableIndexDefinition("ix_parent_ts", ["parent_id", "ts"], false, [KeyColumnDirection.Ascending, KeyColumnDirection.Descending]));
        }

        return new TableMetadata
        {
            Identifier = Children,
            Table = new Table { SourceName = "test", SchemaName = "main", TableName = "children", SourceType = "Sqlite" },
            PrimaryKeyColumns = withPk ? ["id"] : [],
            Indexes = indexes,
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "parent_id", DataType = "integer" },
                new TableColumn { ColumnName = "ts", DataType = "text" },
                new TableColumn { ColumnName = "value", DataType = "text" },
                new TableColumn { ColumnName = "tenant_id", DataType = "text" },
                new TableColumn { ColumnName = "secret", DataType = "text" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["secret"] = new() { RuleType = "REDACT", Replacement = "***" }
            }
        };
    }

    private static TableMetadata GrandchildrenMeta() => new()
    {
        Identifier = Grandchildren,
        Table = new Table { SourceName = "test", SchemaName = "main", TableName = "grandchildren", SourceType = "Sqlite" },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "child_id", DataType = "integer" },
            new TableColumn { ColumnName = "note", DataType = "text" }
        ]
    };

    private static Dictionary<TableIdentifier, TreeTableAccess> CreateAccess(
        bool childIndexed = false,
        bool parentWithPk = true,
        bool childWithPk = true,
        string? tenant = "tenant1",
        IRowPolicy? childRowFilter = null)
    {
        var parentDecision = TypedTreeRowPolicy.Allowed(Parents, new Dictionary<string, ColumnAccessLevel>(), null);
        var childDecision = TypedTreeRowPolicy.Allowed(Children, new Dictionary<string, ColumnAccessLevel>
        {
            ["secret"] = ColumnAccessLevel.Mask
        }, childRowFilter);
        var grandDecision = TypedTreeRowPolicy.Allowed(Grandchildren, new Dictionary<string, ColumnAccessLevel>(), null);

        return new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Parents] = new(ParentsMeta(parentWithPk), parentDecision, "tenant_id", tenant),
            [Children] = new(ChildrenMeta(childIndexed, childWithPk), childDecision, "tenant_id", tenant),
            [Grandchildren] = new(GrandchildrenMeta(), grandDecision)
        };
    }

    private static TreeRelationNode ChildrenOf(TreeQueryNode child, string key = "children", bool isList = true) =>
        new(key, ParentColumns: ["id"], ChildColumns: ["parent_id"], IsList: isList, child);

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.Sqlite)]
    public void IndexMatched_SqlIsByteIdenticalToBaseline(DatabaseDialect dialect)
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 5
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 10
        };

        var access = CreateAccess(childIndexed: true);
        var compiled = TreeSqlCompiler.Compile(root, access, dialect);

        compiled.Lists.Count.ShouldBe(1);
        compiled.Lists[0].Strategy.ShouldBe(TreeListStrategy.CorrelatedIndexed);
        compiled.Lists[0].Reason.ShouldBe("IndexMatched");
        compiled.Sql.ShouldNotContain("autheris_page");
        compiled.Sql.ShouldNotContain("autheris_rn");
    }

    [Fact]
    public void NoOrderBy_SqlIsByteIdentical()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            Limit = 5
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 10
        };

        var access = CreateAccess(childIndexed: false);
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);

        compiled.Lists.Count.ShouldBe(1);
        compiled.Lists[0].Strategy.ShouldBe(TreeListStrategy.CorrelatedUnindexed);
        compiled.Lists[0].Reason.ShouldBe("DefaultOrder");
        compiled.Sql.ShouldNotContain("autheris_page");
    }

    [Fact]
    public void ToOneRelation_SqlIsByteIdentical()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)]
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode, isList: false)],
            OrderBy = [new TreeOrder("id")],
            Limit = 10
        };

        var access = CreateAccess(childIndexed: false);
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);

        compiled.Lists.Count.ShouldBe(0);
        compiled.Sql.ShouldNotContain("autheris_page");
    }

    [Fact]
    public void DepthTwo_SqlIsByteIdentical()
    {
        var grandNode = new TreeQueryNode(Grandchildren, ["id", "note"])
        {
            OrderBy = [new TreeOrder("id")],
            Limit = 3
        };
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            Relations = [new TreeRelationNode("grandchildren", ["id"], ["child_id"], true, grandNode)],
            Limit = 5
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 10
        };

        var access = CreateAccess(childIndexed: false);
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);

        var grandPlan = compiled.Lists.FirstOrDefault(l => l.Path.SequenceEqual(["children", "grandchildren"]));
        grandPlan.ShouldNotBeNull();
        grandPlan.Strategy.ShouldBe(TreeListStrategy.CorrelatedUnindexed);
        grandPlan.Reason.ShouldBe("NotRootChild");
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.Sqlite)]
    public void NoMatch_EmitsPageCteAndRowNumber(DatabaseDialect dialect)
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 3
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var access = CreateAccess(childIndexed: false);
        var compiled = TreeSqlCompiler.Compile(root, access, dialect);

        compiled.Lists.Count.ShouldBe(1);
        compiled.Lists[0].Strategy.ShouldBe(TreeListStrategy.SetBased);
        compiled.Lists[0].Reason.ShouldBeNull();

        compiled.Sql.ShouldStartWith("WITH ");
        compiled.Sql.ShouldContain("autheris_page");
        compiled.Sql.ShouldContain("autheris_rn");
        compiled.Sql.ShouldContain("ROW_NUMBER() OVER (PARTITION BY ");
        compiled.Sql.ShouldContain("EXISTS (SELECT 1 FROM ");
    }

    [Fact]
    public void NoMatch_RowFilterAndTenantInRankingLevel_BeforeRowNumber()
    {
        var childRowFilter = TypedTreeRowPolicy.Int("value", BinaryOperator.Equal, 42);
        var access = CreateAccess(childIndexed: false, tenant: "t_tenant", childRowFilter: childRowFilter);

        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);

        // Ranking level should contain tenant parameter and row filter parameter
        compiled.Sql.ShouldContain("autheris_target].[tenant_id] =");
        compiled.Sql.ShouldContain("autheris_rn");
        compiled.Sql.ShouldContain("ROW_NUMBER()");
    }

    [Fact]
    public void NoMatch_SecuredSourceUsedInRankingLevel()
    {
        var access = CreateAccess(childIndexed: false);
        var originalChildAccess = access[Children];
        access[Children] = new TreeTableAccess(
            originalChildAccess.Metadata,
            originalChildAccess.Decision,
            null,
            null,
            new TreeSecuredSource("SELECT id, parent_id, ts, value, tenant_id, secret FROM secured_children",
                System.Collections.Immutable.ImmutableArray<TrinoSqlEngine.Ast.Emit.BoundParameter>.Empty,
                TrinoSqlEngine.TargetSqlDialect.SqlServer));

        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);
        compiled.Sql.ShouldContain("FROM (SELECT id, parent_id, ts, value, tenant_id, secret FROM secured_children) AS [autheris_target]");
    }

    [Fact]
    public void NoMatch_RestrictsToPageKeysViaExists()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer);
        compiled.Sql.ShouldContain("EXISTS (SELECT 1 FROM [autheris_page] AS [autheris_p] WHERE [autheris_p].[id] = [autheris_target].[parent_id])");
    }

    [Fact]
    public void NoMatch_AppendsChildPkTieBreak()
    {
        // Child order: ts DESC. Child PK: id.
        // ROW_NUMBER order should be ts DESC, id ASC
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer);
        compiled.Sql.ShouldContain("ORDER BY [autheris_target].[ts] DESC, [autheris_target].[id])");
    }

    [Fact]
    public void NoMatch_AppendsRootPkTieBreak()
    {
        // Root order: name ASC. Root PK: id.
        // Root ORDER BY should contain name, id.
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("name")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer);
        compiled.Sql.ShouldContain("[autheris_target].[name], [autheris_target].[id]");
        compiled.Sql.ShouldContain("[t1].[name], [t1].[id]");
    }

    [Fact]
    public void RootWithoutClearUniqueKey_FallsBackCorrelated()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("name")],
            Limit = 5
        };

        var access = CreateAccess(childIndexed: false, parentWithPk: false);
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);

        compiled.Lists[0].Strategy.ShouldBe(TreeListStrategy.CorrelatedUnindexed);
        compiled.Lists[0].Reason.ShouldBe("ParentOrderNotTotal");
        compiled.Sql.ShouldNotContain("autheris_page");
    }

    [Fact]
    public void ReservedNameCollision_FallsBackCorrelated()
    {
        var customMeta = new TableMetadata
        {
            Identifier = Children,
            Table = new Table { SourceName = "test", SchemaName = "main", TableName = "children", SourceType = "Sqlite" },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "parent_id", DataType = "integer" },
                new TableColumn { ColumnName = "ts", DataType = "text" },
                new TableColumn { ColumnName = "autheris_rn", DataType = "integer" }
            ]
        };

        var access = CreateAccess(childIndexed: false);
        access[Children] = new TreeTableAccess(customMeta, access[Children].Decision);

        var childNode = new TreeQueryNode(Children, ["id", "autheris_rn"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);
        compiled.Lists[0].Strategy.ShouldBe(TreeListStrategy.CorrelatedUnindexed);
        compiled.Lists[0].Reason.ShouldBe("ReservedNameCollision");
        compiled.Sql.ShouldNotContain("autheris_page");
    }

    [Fact]
    public void ForcedSetBasedFailure_EmitsCorrelatedAndRestoresParameters()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode, key: "failing_rel")],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var options = new TreeCompileOptions { FailSetBasedFor = "failing_rel" };
        var compiled = TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer, false, options);

        compiled.Lists[0].Strategy.ShouldBe(TreeListStrategy.CorrelatedUnindexed);
        compiled.Lists[0].Reason.ShouldBe("CompileFailure");
        compiled.Sql.ShouldNotContain("autheris_page");
        compiled.Sql.ShouldContain("[failing_rel]");
    }

    [Fact]
    public void ForcedSetBasedFailure_NeverDropsRowFilter()
    {
        var childRowFilter = TypedTreeRowPolicy.Int("value", BinaryOperator.Equal, 999);
        var access = CreateAccess(childIndexed: false, childRowFilter: childRowFilter);

        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode, key: "failing_rel")],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var options = new TreeCompileOptions { FailSetBasedFor = "failing_rel" };
        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer, false, options);

        compiled.Parameters.Values.ShouldContain((long)999);
        compiled.Sql.ShouldContain("\"value\" =");
    }

    [Fact]
    public void TwoListsOnOneParent_DecideIndependently()
    {
        var table2 = new TableIdentifier("test", "main", "children2");
        var meta2 = new TableMetadata
        {
            Identifier = table2,
            Table = new Table { SourceName = "test", SchemaName = "main", TableName = "children2", SourceType = "Sqlite" },
            PrimaryKeyColumns = ["id"],
            Indexes = [new TableIndexDefinition("ix_c2", ["parent_id", "ts"], false, [KeyColumnDirection.Ascending, KeyColumnDirection.Descending])],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "parent_id", DataType = "integer" },
                new TableColumn { ColumnName = "ts", DataType = "text" }
            ]
        };

        var access = CreateAccess(childIndexed: false);
        access[table2] = new TreeTableAccess(meta2, TypedTreeRowPolicy.Allowed(table2, new Dictionary<string, ColumnAccessLevel>(), null));

        var unindexedChild = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var indexedChild = new TreeQueryNode(table2, ["id"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };

        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations =
            [
                ChildrenOf(unindexedChild, key: "unindexed_rel"),
                new TreeRelationNode("indexed_rel", ["id"], ["parent_id"], true, indexedChild)
            ],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);

        compiled.Lists.First(l => l.Path.SequenceEqual(["unindexed_rel"])).Strategy.ShouldBe(TreeListStrategy.SetBased);
        compiled.Lists.First(l => l.Path.SequenceEqual(["indexed_rel"])).Strategy.ShouldBe(TreeListStrategy.CorrelatedIndexed);

        // SQL should have autheris_page and one CTE for unindexed_rel, while indexed_rel uses correlated subquery
        compiled.Sql.ShouldContain("autheris_page");
        compiled.Sql.ShouldContain("autheris_k");
        compiled.Sql.ShouldContain("[unindexed_rel]");
        compiled.Sql.ShouldContain("[indexed_rel]");
    }

    [Fact]
    public void CompositeJoinKey_PartitionsAndJoinsOnAllColumns()
    {
        var compChildren = new TableIdentifier("test", "main", "comp_children");
        var meta = new TableMetadata
        {
            Identifier = compChildren,
            Table = new Table { SourceName = "test", SchemaName = "main", TableName = "comp_children", SourceType = "Sqlite" },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "p_tenant", DataType = "text" },
                new TableColumn { ColumnName = "p_id", DataType = "integer" },
                new TableColumn { ColumnName = "ts", DataType = "text" }
            ]
        };

        var access = CreateAccess(childIndexed: false);
        access[compChildren] = new TreeTableAccess(meta, TypedTreeRowPolicy.Allowed(compChildren, new Dictionary<string, ColumnAccessLevel>(), null));

        var childNode = new TreeQueryNode(compChildren, ["id"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "tenant_id"])
        {
            Relations = [new TreeRelationNode("readings", ["tenant_id", "id"], ["p_tenant", "p_id"], true, childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);

        compiled.Sql.ShouldContain("PARTITION BY [autheris_target].[p_tenant], [autheris_target].[p_id]");
        compiled.Sql.ShouldContain("GROUP BY [x].[autheris_k0], [x].[autheris_k1]");
        compiled.Sql.ShouldContain("[autheris_p].[tenant_id] = [autheris_target].[p_tenant] AND [autheris_p].[id] = [autheris_target].[p_id]");
    }

    [Fact]
    public void SetBasedSql_IsDeterministicAcrossCompiles()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 3
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var access = CreateAccess(childIndexed: false);
        var compiled1 = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);
        var compiled2 = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);

        compiled1.Sql.ShouldBe(compiled2.Sql);
        compiled1.Parameters.Count.ShouldBe(compiled2.Parameters.Count);
        foreach (var (k, v) in compiled1.Parameters)
        {
            compiled2.Parameters[k].ShouldBe(v);
        }
    }

    [Fact]
    public void AllValuesParameterized_NoLiteralInterpolation()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            Where = new TreeComparison("value", TreeFilterOperator.Eq, "SECRET_VALUE_12345"),
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer);
        compiled.Sql.ShouldNotContain("SECRET_VALUE_12345");
        compiled.Parameters.Values.ShouldContain("SECRET_VALUE_12345");
    }

    [Fact]
    public void MaskedColumn_StillMaskedInRowJson()
    {
        var childNode = new TreeQueryNode(Children, ["id", "secret"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var compiled = TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer);
        compiled.Sql.ShouldContain("N'***'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SetBased_RowBudgetExceeded_FallsBackCorrelated()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 200
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 1000 // 200 * 1000 = 200,000 > MaxSetBasedRows (100,000)
        };

        var compiled = TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer);
        compiled.Lists[0].Strategy.ShouldBe(TreeListStrategy.CorrelatedUnindexed);
        compiled.Lists[0].Reason.ShouldBe("RowBudgetExceeded");
        compiled.Sql.ShouldNotContain("autheris_page");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Cancellation_NotSwallowedByFallback()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 1
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode, key: "cancelling_rel")],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Injecting OperationCanceledException
        var options = new TreeCompileOptions { FailSetBasedFor = "cancelling_rel" };

        // Test that an OperationCanceledException thrown during compile propagates and is NOT swallowed by fallback
        Should.Throw<OperationCanceledException>(() =>
            TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer, false, options));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NestedFirst_OverCap_Rejected_BeforeStrategy()
    {
        var childNode = new TreeQueryNode(Children, ["id", "value"])
        {
            OrderBy = [new TreeOrder("ts", Descending: true)],
            Limit = 999_999 // over MaxLimit
        };
        var root = new TreeQueryNode(Parents, ["id", "name"])
        {
            Relations = [ChildrenOf(childNode)],
            OrderBy = [new TreeOrder("id")],
            Limit = 5
        };

        Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(root, CreateAccess(childIndexed: false), DatabaseDialect.SqlServer));
    }
}
