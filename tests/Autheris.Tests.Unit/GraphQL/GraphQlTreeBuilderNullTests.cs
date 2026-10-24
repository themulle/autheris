using Autheris.Application.Analytics;
using Autheris.Domain.Exceptions;
using Autheris.GraphQL.Catalog;
using HotChocolate.Language;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#300 GAP-009: an explicit null operand of a comparison is a bound NULL (3VL), never IS NULL, a literal or a FALSE constant.</summary>
public sealed class GraphQlTreeBuilderNullTests
{
    private static readonly TableIdentifier Logs = new("analytics", "main", "audit_logs");

    private static TableMetadata Meta() => new()
    {
        Identifier = Logs,
        Table = new Table
        {
            SourceName = "analytics",
            SchemaName = "main",
            TableName = "audit_logs",
            SourceType = "Sqlite",
            RequiredFilterColumns = ["tenant_id"]
        },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "tenant_id", DataType = "text" },
            new TableColumn { ColumnName = "action", DataType = "text" }
        ]
    };

    private static ColumnRef? Resolve(string name) => name switch
    {
        "tenant_id" or "action" => new ColumnRef(name, ColumnValueKind.String),
        _ => null
    };

    private static TreeComparison NullCmp(string column, TreeFilterOperator op) =>
        new(column, op, null) { BindNullOperand = true };

    [Theory]
    [InlineData(TreeFilterOperator.Eq, "=")]
    [InlineData(TreeFilterOperator.Neq, "<>")]
    [InlineData(TreeFilterOperator.Gt, ">")]
    [InlineData(TreeFilterOperator.Gte, ">=")]
    [InlineData(TreeFilterOperator.Lt, "<")]
    [InlineData(TreeFilterOperator.Lte, "<=")]
    [InlineData(TreeFilterOperator.Contains, "LIKE")]
    public void EqNull_BuildsNullComparison(TreeFilterOperator op, string symbol)
    {
        var clause = TreeFilterClauseBuilder.Build(NullCmp("action", op), Resolve);

        clause.SqlPredicate.ShouldContain($" {symbol} @__gql_tf_0");
        clause.SqlPredicate.ShouldNotContain("IS NULL");
        clause.SqlPredicate.ShouldNotContain("IS NOT NULL");
        clause.Parameters["@__gql_tf_0"].ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NullComparison_UsesBoundNull_NoLiteral()
    {
        // SEC-PG-06: no `= NULL` text, no FALSE constant; NOT and OR stay three-valued.
        var filter = new TreeNotFilter(new TreeOrFilter([NullCmp("action", TreeFilterOperator.Eq), NullCmp("action", TreeFilterOperator.Lt)]));
        var clause = TreeFilterClauseBuilder.Build(filter, Resolve);

        clause.SqlPredicate.ShouldNotContain("NULL");
        clause.SqlPredicate.ShouldNotContain("1 = 0");
        clause.SqlPredicate.ShouldStartWith("NOT (");
        clause.Parameters.Values.ShouldAllBe(v => v == null);
        clause.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void WithoutTheFlag_EqNull_KeepsIsNullMeaning()
    {
        var clause = TreeFilterClauseBuilder.Build(new TreeComparison("action", TreeFilterOperator.Eq, null), Resolve);
        clause.SqlPredicate.ShouldContain("IS NULL");
    }

    [Fact]
    public void InList_DropsNullItems()
    {
        var clause = TreeFilterClauseBuilder.Build(
            new TreeComparison("action", TreeFilterOperator.In, new List<object?> { "a", null }), Resolve);
        clause.Parameters.Values.ShouldBe(["a"]);

        var empty = TreeFilterClauseBuilder.Build(
            new TreeComparison("action", TreeFilterOperator.In, new List<object?> { null }), Resolve);
        empty.SqlPredicate.ShouldBe("1 = 0");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NotInList_WithNull_IsNeverTrue()
    {
        // #328 (SEC-RG-16): x NOT IN (a, NULL) is never true in three-valued logic, so a list with a null renders no rows (it used to drop the null).
        var onlyNull = TreeFilterClauseBuilder.Build(
            new TreeComparison("action", TreeFilterOperator.NotIn, new List<object?> { null }), Resolve);
        onlyNull.SqlPredicate.ShouldBe("1 = 0");

        var mixed = TreeFilterClauseBuilder.Build(
            new TreeComparison("action", TreeFilterOperator.NotIn, new List<object?> { "a", null }), Resolve);
        mixed.SqlPredicate.ShouldBe("1 = 0");
        mixed.Parameters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void EqNull_RequiredFilterColumn_ReturnsNoRowsAndKeepsPolicy()
    {
        // SEC-PG-06: `tenant_id: {eq: null}` satisfies the required-filter gate but never replaces the tenant predicate.
        var decision = TableAccessDecision.Allowed(Logs, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Logs] = new(Meta(), decision, "tenant_id", "tenant-1", null)
        };
        var root = new TreeQueryNode(Logs, ["id", "action"])
        {
            Where = NullCmp("tenant_id", TreeFilterOperator.Eq),
            Limit = 10
        };

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite);

        compiled.Sql.ShouldContain("\"tenant_id\" = ");
        compiled.Parameters.Values.ShouldContain("tenant-1");
        compiled.Parameters.Values.Count(v => v is null or System.DBNull).ShouldBe(1);
        compiled.Sql.ShouldNotContain("IS NULL");
    }
}

/// <summary>#300: the where-object parser (<c>GraphQlTreeBuilder.ParseObjectFilter</c>) accepts null only for the comparison operators.</summary>
public sealed class GraphQlTreeBuilderNullParseTests
{
    private static readonly TableIdentifier Orders = new("shop", "main", "orders");

    private static CatalogTableType Table() => new(
        Orders, "Orders", "orders", "OrdersFilter", "OrdersOrderBy", new TableMetadata { Identifier = Orders },
        [
            new CatalogColumnField("amount", "amount", CatalogFieldType.Decimal, "decimal", true),
            new CatalogColumnField("note", "note", CatalogFieldType.String, "varchar", true)
        ],
        []);

    private static TreeFilter? Parse(string where) =>
        GraphQlTreeBuilder.ParseObjectFilter(
            (ObjectValueNode)Utf8GraphQLParser.Syntax.ParseValueLiteral(where),
            Table(),
            NSubstitute.Substitute.For<HotChocolate.Resolvers.IResolverContext>());

    [Theory]
    [InlineData("eq", TreeFilterOperator.Eq)]
    [InlineData("neq", TreeFilterOperator.Neq)]
    [InlineData("gt", TreeFilterOperator.Gt)]
    [InlineData("lte", TreeFilterOperator.Lte)]
    [InlineData("contains", TreeFilterOperator.Contains)]
    [Trait("Category", "Security")]
    public void ComparisonNull_SetsBindNullOperand(string op, TreeFilterOperator expected)
    {
        // SEC-PG-06
        var cmp = Parse($"{{ note: {{ {op}: null }} }}").ShouldBeOfType<TreeComparison>();

        cmp.Operator.ShouldBe(expected);
        cmp.Value.ShouldBeNull();
        cmp.BindNullOperand.ShouldBeTrue();
    }

    [Fact]
    public void ComparisonWithValue_DoesNotSetBindNullOperand() =>
        Parse("{ note: { eq: \"x\" } }").ShouldBeOfType<TreeComparison>().BindNullOperand.ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void IsNullNull_StaysRejected() =>
        Should.Throw<GatewayInvalidQueryException>(() => Parse("{ note: { isNull: null } }"));

    [Fact]
    [Trait("Category", "Security")]
    public void ColumnNull_StaysRejected() =>
        Should.Throw<GatewayInvalidQueryException>(() => Parse("{ note: null }"));

    [Theory]
    [InlineData("in")]
    [InlineData("nin")]
    public void ListOperatorNull_IsRejectedWithAFixedMessage(string op)
    {
        var ex = Should.Throw<GatewayInvalidQueryException>(() => Parse($"{{ note: {{ {op}: null }} }}"));
        ex.Message.ShouldBe($"Value for operator '{op}' on field 'note' must be a list.");
    }
}
