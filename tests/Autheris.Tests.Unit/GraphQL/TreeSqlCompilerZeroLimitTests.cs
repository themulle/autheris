using System.Collections.Generic;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#302 GAP-011 / SEC-PG-07: a zero page compiles on every dialect without FETCH NEXT 0 ROWS and keeps the tenant predicate.</summary>
public sealed class TreeSqlCompilerZeroLimitTests
{
    private static readonly TableIdentifier Logs = new("analytics", "main", "audit_logs");

    private static Dictionary<TableIdentifier, TreeTableAccess> Access() => new()
    {
        [Logs] = new(
            new TableMetadata
            {
                Identifier = Logs,
                Table = new Table { SourceName = "analytics", SchemaName = "main", TableName = "audit_logs", SourceType = "Sqlite" },
                PrimaryKeyColumns = ["id"],
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "integer" },
                    new TableColumn { ColumnName = "tenant_id", DataType = "text" }
                ]
            },
            TableAccessDecision.Allowed(Logs, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true),
            "tenant_id",
            "tenant-1")
    };

    [Fact]
    [Trait("Category", "Security")]
    public void LimitZero_SqlServer_NoFetchZero_KeepsTenantPredicate()
    {
        var root = new TreeQueryNode(Logs, ["id"]) { Limit = 0 };

        var compiled = TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.SqlServer);

        compiled.Sql.ShouldNotContain("FETCH NEXT 0");
        compiled.Sql.ShouldContain("1 = 0");
        compiled.Sql.ShouldContain("FETCH NEXT 1 ROWS ONLY");
        compiled.Parameters.Values.ShouldContain("tenant-1");
    }

    [Theory]
    [InlineData(DatabaseDialect.Sqlite)]
    [InlineData(DatabaseDialect.SqlServer)]
    public void LimitZero_IsAccepted_NegativeIsRejected(DatabaseDialect dialect)
    {
        Should.NotThrow(() => TreeSqlCompiler.Compile(new TreeQueryNode(Logs, ["id"]) { Limit = 0 }, Access(), dialect));
        Should.Throw<Autheris.Domain.Exceptions.GatewayInvalidQueryException>(
            () => TreeSqlCompiler.Compile(new TreeQueryNode(Logs, ["id"]) { Limit = -1 }, Access(), dialect));
    }
}
