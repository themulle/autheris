namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.Optimization;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Application.Sql.Federation.Pushdown;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;
using OptimizationBuilder = Autheris.Application.Sql.Federation.Optimization.PushedClauseBuilder;

/// <summary>SQLA-09 (bind client values), SQLA-10 (collation guard for NOT and ranges) and SQLA-12 (table attribution) of the federated pushdown.</summary>
[Trait("Category", "Security")]
public sealed class SqlaPushdownSecurityTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata Table(string table, DatabaseDialect dialect = DatabaseDialect.PostgreSql, bool exactCompare = false) => new()
    {
        Identifier = new TableIdentifier("crm", "public", table),
        Table = new Table
        {
            TableName = table,
            SchemaName = "public",
            SourceType = dialect.ToString(),
            DataSourceType = DataSourceType.Sql,
            SourceName = "crm",
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "int" },
            new TableColumn
            {
                ColumnName = "status",
                DataType = "varchar",
                Meta = exactCompare ? new Dictionary<string, string> { ["exact_compare"] = "true" } : new Dictionary<string, string>()
            },
            new TableColumn { ColumnName = "oid", DataType = "int" }
        ]
    };

    private static SqlStatement Parse(string sql)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory());
        return new SqlAstBuilder().BuildStatement(tree);
    }

    private static TableAccessDecision Clear(TableMetadata meta) =>
        TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

    private static SingleTableConjunct Conjunct(TableMetadata meta, string whereSql, params string[] columns)
    {
        var where = ((QuerySpecification)((SelectStatement)Parse($"SELECT * FROM orders WHERE {whereSql}")).Body).Where!;
        return new SingleTableConjunct(meta.Identifier, where, columns, new ExtractedConjunct(where, ConjunctOrigin.Where));
    }

    private static PushdownEvaluationResult Eval(TableMetadata meta, string whereSql, DatabaseDialect dialect, IReadOnlyDictionary<string, object?>? clientParameters = null, params string[] columns) =>
        PushabilityEvaluator.Evaluate(Conjunct(meta, whereSql, columns), meta, Clear(meta), dialect, null, new PredicatePushdownOptions(), clientParameters);

    // ---- SQLA-09 ----

    [Fact]
    public void Pushdown_EqParameter_BindsClientValue()
    {
        var meta = Table("orders");
        var values = new Dictionary<string, object?> { ["x"] = "100" };
        var conjunct = Conjunct(meta, "status = __param_x", "status");

        Eval(meta, "status = __param_x", DatabaseDialect.PostgreSql, values, "status").IsPushable.ShouldBeTrue();
        var clause = PushedClauseBuilder.Build([conjunct], meta, values);

        clause.Parameters.Values.ShouldContain("100");
        clause.Parameters.Values.ShouldNotContain((object?)null);
    }

    [Fact]
    public void Pushdown_InParameters_BindsValues()
    {
        var meta = Table("orders");
        var values = new Dictionary<string, object?> { ["a"] = "A", ["b"] = "B" };

        var clause = PushedClauseBuilder.Build([Conjunct(meta, "status IN (__param_a, __param_b)", "status")], meta, values);

        clause.Parameters.Values.OrderBy(v => v).ShouldBe(new object?[] { "A", "B" });
    }

    [Fact]
    public void Pushdown_BetweenParameters_BindsValues()
    {
        var meta = Table("orders");
        var values = new Dictionary<string, object?> { ["lo"] = 1, ["hi"] = 9 };

        var clause = PushedClauseBuilder.Build([Conjunct(meta, "amount BETWEEN __param_lo AND __param_hi", "amount")], meta, values);

        clause.Parameters.Values.OrderBy(v => v).ShouldBe(new object?[] { 1, 9 });
    }

    [Fact]
    public void Pushdown_UnknownParameter_NotPushed()
    {
        var meta = Table("orders");
        var empty = new Dictionary<string, object?>();

        var eval = Eval(meta, "status = __param_missing", DatabaseDialect.PostgreSql, empty, "status");

        eval.IsPushable.ShouldBeFalse();
        eval.RejectionReason.ShouldBe(RejectionReason.UnresolvedParameter);
        Should.Throw<PushdownRejectedException>(() => PushedClauseBuilder.Build([Conjunct(meta, "status = __param_missing", "status")], meta, empty));
        Eval(meta, "status = __param_missing", DatabaseDialect.PostgreSql, null, "status").IsPushable.ShouldBeFalse();
    }

    // ---- SQLA-10 ----

    [Fact]
    public void Pushdown_NotEquals_SqlServerCiString_Rejected()
    {
        var meta = Table("orders", DatabaseDialect.SqlServer);

        var eval = Eval(meta, "NOT (status = 'X')", DatabaseDialect.SqlServer, null, "status");

        eval.IsPushable.ShouldBeFalse();
        eval.RejectionReason.ShouldBe(RejectionReason.CollationRisk);
    }

    [Fact]
    public void Pushdown_NotIn_Wrapped_SqlServer_Rejected()
    {
        var meta = Table("orders", DatabaseDialect.SqlServer);

        Eval(meta, "NOT (status IN ('a', 'b'))", DatabaseDialect.SqlServer, null, "status").RejectionReason.ShouldBe(RejectionReason.CollationRisk);
    }

    [Fact]
    public void Pushdown_StringLessThan_SqlServer_Rejected()
    {
        var meta = Table("orders", DatabaseDialect.SqlServer);

        Eval(meta, "status < 'M'", DatabaseDialect.SqlServer, null, "status").RejectionReason.ShouldBe(RejectionReason.CollationRisk);
    }

    [Fact]
    public void Pushdown_StringRange_ExactCompare_Pushed()
    {
        var meta = Table("orders", DatabaseDialect.SqlServer, exactCompare: true);

        Eval(meta, "status < 'M'", DatabaseDialect.SqlServer, null, "status").IsPushable.ShouldBeTrue();
        Eval(meta, "NOT (status = 'X')", DatabaseDialect.SqlServer, null, "status").IsPushable.ShouldBeTrue();
    }

    [Fact]
    public void Pushdown_NumericRange_SqlServer_Pushed()
    {
        var meta = Table("orders", DatabaseDialect.SqlServer);

        Eval(meta, "amount < 5", DatabaseDialect.SqlServer, null, "amount").IsPushable.ShouldBeTrue();
        Eval(meta, "amount BETWEEN 1 AND 5", DatabaseDialect.SqlServer, null, "amount").IsPushable.ShouldBeTrue();
    }

    // ---- SQLA-12 ----

    private static (TableAccessTarget Orders, TableAccessTarget Items, IReadOnlyList<ResolvedSourceTable> All, TableMetadata OrdersMeta, TableMetadata ItemsMeta) TwoTables()
    {
        var ordersMeta = Table("orders");
        var itemsMeta = Table("items");
        var orders = new TableAccessTarget("crm", "public", "orders", "o", "crm.public.orders");
        var items = new TableAccessTarget("crm", "public", "items", "i", "crm.public.items");
        IReadOnlyList<ResolvedSourceTable> all =
        [
            new ResolvedSourceTable(orders, ordersMeta.Identifier, ordersMeta, "crm"),
            new ResolvedSourceTable(items, itemsMeta.Identifier, itemsMeta, "crm")
        ];
        return (orders, items, all, ordersMeta, itemsMeta);
    }

    private static FederatedParameterContract Contract() =>
        FederatedParameterContract.Create(new Dictionary<string, object?> { ["s"] = "open" });

    private static TableFilterClause? Push(string sql, TableAccessTarget target, TableMetadata meta, IReadOnlyList<ResolvedSourceTable> all) =>
        OptimizationBuilder.BuildPushdown(Parse(sql), target, meta, Clear(meta), Contract(), all, out _);

    [Fact]
    public void ParamPushdown_OwnAliasQualifier_Pushed()
    {
        var t = TwoTables();

        Push("SELECT * FROM crm.public.orders o JOIN crm.public.items i ON o.id = i.oid WHERE o.status = __param_s", t.Orders, t.OrdersMeta, t.All).ShouldNotBeNull();
    }

    [Fact]
    public void ParamPushdown_OtherTableQualifier_NotPushed()
    {
        var t = TwoTables();

        Push("SELECT * FROM crm.public.orders o JOIN crm.public.items i ON o.id = i.oid WHERE i.status = __param_s", t.Orders, t.OrdersMeta, t.All).ShouldBeNull();
    }

    [Fact]
    public void ParamPushdown_UnqualifiedAmbiguousColumn_NotPushed()
    {
        var t = TwoTables();

        Push("SELECT * FROM crm.public.orders o JOIN crm.public.items i ON o.id = i.oid WHERE status = __param_s", t.Orders, t.OrdersMeta, t.All).ShouldBeNull();
    }

    [Fact]
    public void ParamPushdown_SchemaQualifier_NotPushed()
    {
        var ordersMeta = Table("orders");
        var orders = new TableAccessTarget("crm", "public", "orders", null, "crm.public.orders");
        IReadOnlyList<ResolvedSourceTable> all = [new ResolvedSourceTable(orders, ordersMeta.Identifier, ordersMeta, "crm")];

        Push("SELECT * FROM crm.public.orders WHERE public.status = __param_s", orders, ordersMeta, all).ShouldBeNull();
    }

    [Fact]
    public void ParamPushdown_NullSupplyingSide_NotPushed()
    {
        var t = TwoTables();

        Push("SELECT * FROM crm.public.orders o LEFT JOIN crm.public.items i ON o.id = i.oid WHERE i.status = __param_s", t.Items, t.ItemsMeta, t.All).ShouldBeNull();
    }

    // ---- SQLA-13 ----

    [Fact]
    public void ParameterPushdown_SkippedCandidate_TrinoFormUsesTheSameMarkersAsTheBoundParameters()
    {
        var meta = Table("orders");
        var contract = FederatedParameterContract.Create(new Dictionary<string, object?> { ["x"] = null, ["y"] = "open" });
        var candidates = new[]
        {
            new PushedConjunctCandidate("id", "=", "x"),
            new PushedConjunctCandidate("status", "=", "y")
        };

        var clause = OptimizationBuilder.BuildClause(meta, Clear(meta), candidates, contract, null, out _);

        clause.ShouldNotBeNull();
        clause.SqlPredicate.ShouldBe("\"status\" = @p_pd_0_0");
        clause.Parameters.Keys.ShouldBe(["@p_pd_0_0"]);
        clause.TrinoSqlFactory!(c => c).ShouldBe("\"status\" = @p_pd_0_0");
    }
}
