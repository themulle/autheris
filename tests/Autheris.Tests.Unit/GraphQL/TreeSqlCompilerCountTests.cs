using System.Collections.Generic;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>The count statement of a connection total carries exactly the predicates of the page statement and nothing else.</summary>
public sealed class TreeSqlCompilerCountTests
{
    private static readonly TableIdentifier Logs = new("analytics", "main", "audit_logs");

    private static Dictionary<TableIdentifier, TreeTableAccess> Access(string? tenantColumn = "tenant_id", ColumnAccessLevel? emailLevel = null)
    {
        var levels = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        if (emailLevel is { } level)
        {
            levels["email"] = level;
        }

        return new()
        {
            [Logs] = new(
                new TableMetadata
                {
                    Identifier = Logs,
                    Table = new Table { SourceName = "analytics", SchemaName = "main", TableName = "audit_logs", SourceType = "PostgreSql" },
                    PrimaryKeyColumns = ["id"],
                    Columns =
                    [
                        new TableColumn { ColumnName = "id", DataType = "integer" },
                        new TableColumn { ColumnName = "status", DataType = "text" },
                        new TableColumn { ColumnName = "email", DataType = "text" },
                        new TableColumn { ColumnName = "tenant_id", DataType = "text" }
                    ]
                },
                TableAccessDecision.Allowed(Logs, levels, null, hasUnconstrainedColumnAllow: true),
                tenantColumn,
                tenantColumn == null ? null : "tenant-1")
        };
    }

    private static TreeQueryNode Node(TreeFilter? where = null) => new(Logs, ["id"]) { Where = where };

    [Fact]
    [Trait("Category", "Security")]
    public void CompileCount_PostgreSql_CountsAsTextWithoutOrderLimitOrOffset()
    {
        var sql = TreeSqlCompiler.CompileCount(Node(), Access(), DatabaseDialect.PostgreSql).Sql;

        sql.ShouldStartWith("SELECT CAST(COUNT(*) AS TEXT) FROM ");
        sql.ShouldNotContain("ORDER BY");
        sql.ShouldNotContain("LIMIT");
        sql.ShouldNotContain("OFFSET");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CompileCount_SqlServer_UsesCountBigWithoutOffsetFetch()
    {
        var sql = TreeSqlCompiler.CompileCount(Node(), Access(), DatabaseDialect.SqlServer).Sql;

        sql.ShouldStartWith("SELECT CAST(COUNT_BIG(*) AS NVARCHAR(20)) FROM ");
        sql.ShouldNotContain("OFFSET");
        sql.ShouldNotContain("FETCH");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CompileCount_Databricks_CountsAsStringAndCastsTenantToBinary()
    {
        var sql = TreeSqlCompiler.CompileCount(Node(), Access(), DatabaseDialect.Databricks).Sql;

        sql.ShouldStartWith("SELECT CAST(COUNT(*) AS STRING) FROM ");
        sql.ShouldContain("AS BINARY");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CompileCount_TenantAndWhere_AreBoundAsParametersNeverSqlText()
    {
        var where = new TreeComparison("status", TreeFilterOperator.Eq, "x' OR 1=1 --");

        var compiled = TreeSqlCompiler.CompileCount(Node(where), Access(), DatabaseDialect.PostgreSql);

        compiled.Sql.ShouldContain(" WHERE ");
        compiled.Sql.ShouldContain("\"status\"");
        compiled.Sql.ShouldNotContain("tenant-1");
        compiled.Sql.ShouldNotContain("OR 1=1");
        compiled.Parameters.Values.ShouldContain("tenant-1");
        compiled.Parameters.Values.ShouldContain("x' OR 1=1 --");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CompileCount_NeverEmitsUnfilteredCount_WhenTenantColumnExists()
    {
        var sql = TreeSqlCompiler.CompileCount(Node(), Access(), DatabaseDialect.PostgreSql).Sql;

        sql.ShouldContain(" WHERE ");
        sql.ShouldContain("\"tenant_id\"");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(ColumnAccessLevel.Mask)]
    [InlineData(ColumnAccessLevel.Deny)]
    public void CompileCount_FilterOnNonClearColumn_IsRejected(ColumnAccessLevel level)
    {
        var where = new TreeComparison("email", TreeFilterOperator.Eq, "a@b.c");

        Should.Throw<Exception>(() => TreeSqlCompiler.CompileCount(Node(where), Access(emailLevel: level), DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void CompileCount_WithRelations_Throws()
    {
        var root = new TreeQueryNode(Logs, ["id"])
        {
            Relations = [new TreeRelationNode("child", ["id"], ["id"], true, new TreeQueryNode(Logs, ["id"]))]
        };

        Should.Throw<ArgumentException>(() => TreeSqlCompiler.CompileCount(root, Access(), DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void CompileCount_UnsupportedDialect_Throws()
    {
        Should.Throw<NotSupportedException>(() => TreeSqlCompiler.CompileCount(Node(), Access(), DatabaseDialect.Oracle));
    }
}
