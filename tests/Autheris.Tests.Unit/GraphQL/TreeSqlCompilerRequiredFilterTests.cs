using System.Collections.Generic;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class TreeSqlCompilerRequiredFilterTests
{
    private static readonly TableIdentifier Logs = new("analytics", "main", "audit_logs");

    private static TableMetadata LogsMeta(string[]? requiredFilterColumns = null) => new()
    {
        Identifier = Logs,
        Table = new Table
        {
            SourceName = "analytics",
            SchemaName = "main",
            TableName = "audit_logs",
            SourceType = "Sqlite",
            RequiredFilterColumns = [.. (requiredFilterColumns ?? ["tenant_id"])]
        },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "tenant_id", DataType = "text" },
            new TableColumn { ColumnName = "action", DataType = "text" },
            new TableColumn { ColumnName = "timestamp", DataType = "text" }
        ]
    };

    [Fact]
    [Trait("Category", "Security")]
    public void GraphQLTree_MissingRequiredFilter_ThrowsRequiredFilterException()
    {
        var meta = LogsMeta(["tenant_id"]);
        var decision = TableAccessDecision.Allowed(Logs, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Logs] = new(meta, decision)
        };

        var rootNode = new TreeQueryNode(Logs, ["id", "action"])
        {
            Where = null,
            Limit = 10
        };

        var ex = Should.Throw<RequiredFilterColumnMissingException>(() =>
            TreeSqlCompiler.Compile(rootNode, access, DatabaseDialect.Sqlite));

        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GraphQLTree_WithComparisonFilter_Passes()
    {
        var meta = LogsMeta(["tenant_id"]);
        var decision = TableAccessDecision.Allowed(Logs, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Logs] = new(meta, decision)
        };

        var rootNode = new TreeQueryNode(Logs, ["id", "action"])
        {
            Where = new TreeComparison("tenant_id", TreeFilterOperator.Eq, "tenant-123"),
            Limit = 10
        };

        var compiled = TreeSqlCompiler.Compile(rootNode, access, DatabaseDialect.Sqlite);
        compiled.ShouldNotBeNull();
        compiled.Sql.ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GraphQLTree_WithTenantPolicy_PassesWithoutClientFilter()
    {
        var meta = LogsMeta(["tenant_id"]);
        var decision = TableAccessDecision.Allowed(Logs, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Logs] = new(meta, decision, TenantColumn: "tenant_id", TenantValue: "tenant-456")
        };

        var rootNode = new TreeQueryNode(Logs, ["id", "action"])
        {
            Where = null,
            Limit = 10
        };

        var compiled = TreeSqlCompiler.Compile(rootNode, access, DatabaseDialect.Sqlite);
        compiled.ShouldNotBeNull();
        compiled.Sql.ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GraphQLTree_OrFilter_Incomplete_ThrowsRequiredFilterException()
    {
        var meta = LogsMeta(["tenant_id"]);
        var decision = TableAccessDecision.Allowed(Logs, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Logs] = new(meta, decision)
        };

        // OR filter where only one branch constrains tenant_id
        var orFilter = new TreeOrFilter([
            new TreeComparison("tenant_id", TreeFilterOperator.Eq, "tenant-123"),
            new TreeComparison("action", TreeFilterOperator.Eq, "login")
        ]);

        var rootNode = new TreeQueryNode(Logs, ["id", "action"])
        {
            Where = orFilter,
            Limit = 10
        };

        var ex = Should.Throw<RequiredFilterColumnMissingException>(() =>
            TreeSqlCompiler.Compile(rootNode, access, DatabaseDialect.Sqlite));

        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing));
    }
}
