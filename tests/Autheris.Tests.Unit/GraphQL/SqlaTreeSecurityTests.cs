using System.Collections.Generic;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using RequiredFilterColumnMissingException = TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>SQLA-06 (row policy with subquery), SQLA-02 (required-filter value shapes) and SQLA-01 (row budget overflow) in the GraphQL tree path.</summary>
[Trait("Category", "Security")]
public sealed class SqlaTreeSecurityTests
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
            RequiredFilterColumns = [.. (requiredFilterColumns ?? [])]
        },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "tenant_id", DataType = "text" },
            new TableColumn { ColumnName = "action", DataType = "text" }
        ]
    };

    private static CompiledTreeQuery Compile(TableAccessDecision decision, TreeFilter? where = null, string[]? required = null)
    {
        var access = new Dictionary<TableIdentifier, TreeTableAccess> { [Logs] = new(LogsMeta(required), decision) };
        var root = new TreeQueryNode(Logs, ["id", "action"]) { Where = where, Limit = 10 };
        return TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite);
    }

    private static TableAccessDecision Decision() =>
        TableAccessDecision.Allowed(Logs, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);

    private static SelectStatement OtherTable() =>
        new(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("other"), null), null, null, null), null, null);

    private static TableAccessDecision WithPolicy(Func<PolicyParameterExpression, Expression> build)
    {
        var namer = new PolicyParameterNamer("__gql_rls_");
        var parameter = namer.Add(new PolicyValue("EU", SqlParameterType.String));
        var policy = new PolicyPredicateRowPolicy(PolicyPredicate.Create(build(parameter), namer.Values), TargetSqlDialect.Sqlite, new PolicyPredicateRenderer());
        return Decision().WithAdditionalRowPolicy(policy);
    }

    // ---- SQLA-06 ----

    [Fact]
    public void Tree_RowPolicyWithInSubquery_IsDenied()
    {
        var decision = WithPolicy(p => new InSubqueryExpression(PolicyParameterNamer.Column("action"), OtherTable(), false));

        var ex = Should.Throw<GatewaySecurityException>(() => Compile(decision));
        ex.ErrorCode.ShouldBe("TREE_ROW_POLICY_UNSUPPORTED");
    }

    [Fact]
    public void Tree_RowPolicyWithExists_IsDenied()
    {
        var decision = WithPolicy(p => new ExistsExpression(OtherTable()));

        Should.Throw<GatewaySecurityException>(() => Compile(decision));
    }

    [Fact]
    public void Tree_UntypedRowFilterText_IsDenied()
    {
        var decision = TableAccessDecision.Allowed(Logs, new Dictionary<string, ColumnAccessLevel>(), "\"autheris_target\".\"action\" = 'x'", hasUnconstrainedColumnAllow: true);

        Should.Throw<GatewaySecurityException>(() => Compile(decision));
    }

    [Fact]
    public void Tree_SimpleTypedRowPolicy_StillCompiles()
    {
        var decision = WithPolicy(p => new BinaryExpression(PolicyParameterNamer.Column("action"), BinaryOperator.Equal, p));

        Compile(decision).Sql.ShouldNotBeEmpty();
    }

    // ---- SQLA-02 ----

    [Fact]
    public void GraphQLTree_IsNullFalse_DoesNotSatisfyRequiredFilter() =>
        Should.Throw<RequiredFilterColumnMissingException>(() =>
            Compile(Decision(), new TreeComparison("tenant_id", TreeFilterOperator.IsNull, false), ["tenant_id"]));

    [Fact]
    public void GraphQLTree_IsNullTrue_Satisfies() =>
        Compile(Decision(), new TreeComparison("tenant_id", TreeFilterOperator.IsNull, true), ["tenant_id"]).Sql.ShouldNotBeEmpty();

    [Fact]
    public void GraphQLTree_EqNull_DoesNotSatisfy() =>
        Should.Throw<RequiredFilterColumnMissingException>(() =>
            Compile(Decision(), new TreeComparison("tenant_id", TreeFilterOperator.Eq, null), ["tenant_id"]));

    [Fact]
    public void GraphQLTree_InOnlyNulls_DoesNotSatisfy() =>
        Should.Throw<RequiredFilterColumnMissingException>(() =>
            Compile(Decision(), new TreeComparison("tenant_id", TreeFilterOperator.In, new object?[] { null }), ["tenant_id"]));

    // ---- SQLA-01 ----

    private static TreeQueryNode Chain(int levels, int limit)
    {
        var node = new TreeQueryNode(Logs, ["id"]) { Limit = limit };
        for (var i = 1; i < levels; i++)
        {
            node = new TreeQueryNode(Logs, ["id"])
            {
                Limit = limit,
                Relations = [new TreeRelationNode("child" + i, ["id"], ["id"], true, node)]
            };
        }

        return node;
    }

    [Fact]
    public void EstimateRows_Overflow_SaturatesToMax()
    {
        var estimate = GovernedTreeQueryService.EstimateRows(Chain(8, 10_001), 1, isList: true, cap: long.MaxValue);

        estimate.ShouldBe(long.MaxValue);
    }

    [Fact]
    public void AggregateRowBudget_FourNestedMaxLimits_Rejected()
    {
        var estimate = GovernedTreeQueryService.EstimateRows(Chain(5, 10_001), 1, isList: true, cap: 50_000);

        (estimate > 50_000).ShouldBeTrue();
    }

    [Fact]
    public void AggregateRowBudget_WithinBudget_Passes()
    {
        var estimate = GovernedTreeQueryService.EstimateRows(Chain(2, 100), 1, isList: true, cap: 50_000);

        estimate.ShouldBe(100 + 100 * 100);
    }
}
