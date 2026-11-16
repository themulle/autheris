using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// G2/G4 (docs/plans/rls-subquery-in-strategy.md): pure compilation, rejection and dialect-shape unit tests for
/// <see cref="TreeSqlCompiler"/>. Database execution tests run against PostgreSQL in <c>TreeSqlCompilerIntegrationTests</c>.
/// </summary>
public sealed class TreeSqlCompilerTests
{
    private static readonly TableIdentifier Authors = new("blog", "main", "authors");
    private static readonly TableIdentifier Articles = new("blog", "main", "articles");

    private static TableMetadata AuthorsMeta() => new()
    {
        Identifier = Authors,
        Table = new Table { SourceName = "blog", SchemaName = "main", TableName = "authors", SourceType = "PostgreSql" },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "name", DataType = "text" },
            new TableColumn { ColumnName = "email", DataType = "text" },
            new TableColumn { ColumnName = "tenant_id", DataType = "text" },
            new TableColumn { ColumnName = "rating", DataType = "integer" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } }
    };

    private static TableMetadata ArticlesMeta() => new()
    {
        Identifier = Articles,
        Table = new Table { SourceName = "blog", SchemaName = "main", TableName = "articles", SourceType = "PostgreSql" },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "author_id", DataType = "integer" },
            new TableColumn { ColumnName = "title", DataType = "text" },
            new TableColumn { ColumnName = "published", DataType = "integer" },
            new TableColumn { ColumnName = "secret", DataType = "text" }
        ]
    };

    private static Dictionary<TableIdentifier, TreeTableAccess> Access(
        IRowPolicy? articlesRowFilter = null,
        IRowPolicy? authorsRowFilter = null,
        string? tenant = null)
    {
        var authorsDecision = TypedTreeRowPolicy.Allowed(Authors,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny },
            authorsRowFilter);
        var articlesDecision = TypedTreeRowPolicy.Allowed(Articles,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["secret"] = ColumnAccessLevel.Deny },
            articlesRowFilter);
        return new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Authors] = new(AuthorsMeta(), authorsDecision, tenant == null ? null : "tenant_id", tenant),
            [Articles] = new(ArticlesMeta(), articlesDecision)
        };
    }

    private static TreeRelationNode ArticlesOf(TreeQueryNode child, string key = "articles") =>
        new(key, ParentColumns: ["id"], ChildColumns: ["author_id"], IsList: true, child);

    [Fact]
    public void DeniedColumn_IsRejected()
    {
        var root = new TreeQueryNode(Articles, ["id", "secret"]);

        Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql))
            .Message.ShouldContain("secret");
    }

    [Fact]
    public void UnknownColumn_IsRejected_WithTheSameMessageShapeAsDenied()
    {
        var denied = Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(Articles, ["secret"]), Access(), DatabaseDialect.PostgreSql));
        var unknown = Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(Articles, ["nope"]), Access(), DatabaseDialect.PostgreSql));

        denied.Message.Replace("secret", "X").ShouldBe(unknown.Message.Replace("nope", "X"));
    }

    [Theory]
    [InlineData("email", true)]  // masked
    [InlineData("tenant_id", false)] // denied
    public void FilterOrSortOnNonClearColumn_IsRejected(string column, bool isMasked)
    {
        var filtered = new TreeQueryNode(Authors, ["id"]) { Where = new TreeComparison(column, TreeFilterOperator.Eq, "x") };
        var sorted = new TreeQueryNode(Authors, ["id"]) { OrderBy = [new TreeOrder(column)] };

        if (isMasked)
        {
            var exFilter = Should.Throw<FilterOnMaskedColumnException>(() => TreeSqlCompiler.Compile(filtered, Access(), DatabaseDialect.PostgreSql));
            exFilter.ErrorCode.ShouldBe("FILTER_ON_MASKED_COLUMN");
            var exSort = Should.Throw<FilterOnMaskedColumnException>(() => TreeSqlCompiler.Compile(sorted, Access(), DatabaseDialect.PostgreSql));
            exSort.ErrorCode.ShouldBe("FILTER_ON_MASKED_COLUMN");
        }
        else
        {
            var exFilter = Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(filtered, Access(), DatabaseDialect.PostgreSql));
            exFilter.ErrorCode.ShouldBe("INVALID_QUERY");
            var exSort = Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(sorted, Access(), DatabaseDialect.PostgreSql));
            exSort.ErrorCode.ShouldBe("INVALID_QUERY");
        }
    }

    [Fact]
    public void RenameParameters_LeavesLiteralsAndQuotedIdentifiersAlone()
    {
        var renamed = TreeSqlCompiler.RenameParameters(
            "\"@rf\" = @rf AND note = '@rf' AND x = @rfx",
            new Dictionary<string, string> { ["rf"] = "@rf7_rf" });

        renamed.ShouldBe("\"@rf\" = @rf7_rf AND note = '@rf' AND x = @rfx");
    }

    [Fact]
    public void JoinKeyThatIsNotClear_IsRejected()
    {
        var access = Access();
        var articles = access[Articles];
        access[Articles] = articles with
        {
            Decision = TableAccessDecision.Allowed(Articles,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["author_id"] = ColumnAccessLevel.Mask },
                hasUnconstrainedColumnAllow: true)
        };
        var root = new TreeQueryNode(Authors, ["id"]) { Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id"]))] };

        Should.Throw<FilterOnMaskedColumnException>(() => TreeSqlCompiler.Compile(root, access, DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void FilterOrSortOnMaskedColumn_ThrowsFilterOnMaskedColumnException()
    {
        var access = Access();
        var authors = access[Authors];
        access[Authors] = authors with
        {
            Decision = TableAccessDecision.Allowed(Authors,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["email"] = ColumnAccessLevel.Mask },
                hasUnconstrainedColumnAllow: true)
        };

        var filtered = new TreeQueryNode(Authors, ["id"])
        {
            Where = new TreeComparison("email", TreeFilterOperator.Eq, "test@example.com")
        };
        var exFilter = Should.Throw<FilterOnMaskedColumnException>(() => TreeSqlCompiler.Compile(filtered, access, DatabaseDialect.PostgreSql));
        exFilter.ErrorCode.ShouldBe("FILTER_ON_MASKED_COLUMN");
        exFilter.ColumnName.ShouldBe("email");

        var sorted = new TreeQueryNode(Authors, ["id"])
        {
            OrderBy = [new TreeOrder("email", Descending: false)]
        };
        var exSort = Should.Throw<FilterOnMaskedColumnException>(() => TreeSqlCompiler.Compile(sorted, access, DatabaseDialect.PostgreSql));
        exSort.ErrorCode.ShouldBe("FILTER_ON_MASKED_COLUMN");
    }

    [Fact]
    public void FilterOnUnknownOrDeniedColumn_ThrowsGatewayInvalidQuery_WithInvalidQueryCode()
    {
        var access = Access();
        var unknown = new TreeQueryNode(Authors, ["id"])
        {
            Where = new TreeComparison("nonexistent", TreeFilterOperator.Eq, "val")
        };
        var exUnknown = Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(unknown, access, DatabaseDialect.PostgreSql));
        exUnknown.ErrorCode.ShouldBe("INVALID_QUERY");

        var authors = access[Authors];
        access[Authors] = authors with
        {
            Decision = TableAccessDecision.Allowed(Authors,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["email"] = ColumnAccessLevel.Deny },
                hasUnconstrainedColumnAllow: true)
        };
        var denied = new TreeQueryNode(Authors, ["id"])
        {
            Where = new TreeComparison("email", TreeFilterOperator.Eq, "val")
        };
        var exDenied = Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(denied, access, DatabaseDialect.PostgreSql));
        exDenied.ErrorCode.ShouldBe("INVALID_QUERY");
    }

    [Fact]
    public void MissingOrDeniedTableAccess_FailsClosed()
    {
        var access = Access();
        access.Remove(Articles);
        var root = new TreeQueryNode(Authors, ["id"]) { Relations = [ArticlesOf(new TreeQueryNode(Articles, ["id"]))] };

        Should.Throw<GatewayForbiddenException>(() => TreeSqlCompiler.Compile(root, access, DatabaseDialect.PostgreSql));

        var denied = Access();
        denied[Articles] = denied[Articles] with { Decision = TableAccessDecision.Denied(Articles, "no consent") };
        Should.Throw<GatewayForbiddenException>(() => TreeSqlCompiler.Compile(root, denied, DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void DepthAboveLimit_IsRejected()
    {
        TreeQueryNode node = new(Articles, ["id"]);
        for (var i = 0; i < TreeSqlCompiler.MaxDepth; i++)
        {
            node = new TreeQueryNode(Authors, ["id"]) { Relations = [ArticlesOf(node, $"r{i}")] };
        }

        Should.Throw<GatewayInvalidQueryException>(() => TreeSqlCompiler.Compile(node, Access(), DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void SqlServer_UsesForJsonPath_WithNestedJsonQuery()
    {
        var root = new TreeQueryNode(Authors, ["id", "name"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["title"]) { Limit = 5 })],
            OrderBy = [new TreeOrder("id")],
            Limit = 10
        };

        var sql = TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.SqlServer).Sql;

        sql.ShouldContain("FOR JSON PATH, INCLUDE_NULL_VALUES");
        sql.ShouldContain("JSON_QUERY(");
        sql.ShouldContain("OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY");
        sql.ShouldContain("TOP (5)");
        sql.ShouldContain("[autheris_target]");
    }

    [Fact]
    public void PostgreSql_UsesJsonbAggregation()
    {
        var root = new TreeQueryNode(Authors, ["id", "name"])
        {
            Relations = [ArticlesOf(new TreeQueryNode(Articles, ["title"]))],
            OrderBy = [new TreeOrder("id")]
        };

        var sql = TreeSqlCompiler.Compile(root, Access(), DatabaseDialect.PostgreSql).Sql;

        sql.ShouldContain("jsonb_agg(");
        sql.ShouldContain("jsonb_build_object(");
        sql.ShouldContain("\"autheris_target\"");
    }
}
