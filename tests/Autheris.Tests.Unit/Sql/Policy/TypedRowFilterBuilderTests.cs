using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>WP-X0d: typed row filters (consent filters become a <see cref="PolicyPredicate"/>) and the IR renderer.</summary>
public sealed class TypedRowFilterBuilderTests
{
    private static readonly TableIdentity Orders = new("main", "orders");
    private readonly TypedRowFilterBuilder _builder = new();
    private readonly PolicyPredicateRenderer _renderer = new();

    private static ConsentRowFilter Filter(string column, string op, string valueJson, int group = 0) => new()
    {
        ColumnName = column,
        Operator = op,
        ValueJson = valueJson,
        FilterGroup = group
    };

    [Theory]
    [InlineData("EQ", "\"region\" = @__gql_rls_0")]
    [InlineData("NEQ", "\"region\" <> @__gql_rls_0")]
    [InlineData("LT", "\"region\" < @__gql_rls_0")]
    [InlineData("GT", "\"region\" > @__gql_rls_0")]
    [InlineData("LTE", "\"region\" <= @__gql_rls_0")]
    [InlineData("GTE", "\"region\" >= @__gql_rls_0")]
    [InlineData("LIKE", "\"region\" LIKE @__gql_rls_0 ESCAPE ''")]
    public void BuildsTypedPredicate_ForEveryOperator(string op, string expectedSql)
    {
        var predicate = _builder.Build([Filter("region", op, "\"EU\"")], Orders);

        predicate.ReferencedColumns.ShouldContain("region");
        var rendered = _renderer.Render(predicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);
        rendered.Sql.ShouldBe(expectedSql);
        rendered.Parameters["@__gql_rls_0"].ShouldBe("EU");
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "[region] LIKE @__gql_rls_0 ESCAPE '\\'")]
    [InlineData(TargetSqlDialect.Databricks, "`region` LIKE @__gql_rls_0 ESCAPE '\\\\'")]
    [InlineData(TargetSqlDialect.PostgreSql, "\"region\" LIKE @__gql_rls_0 ESCAPE ''")]
    public void ConsentLike_RendersOnEveryDialect_WithTheRenderedPolicyMarkerAsPattern(TargetSqlDialect dialect, string expectedSql)
    {
        var predicate = _builder.Build([Filter("region", "LIKE", "\"EU%\"")], Orders);

        var rendered = _renderer.Render(predicate, dialect, RowFilterAliases.Target);

        rendered.Sql.ShouldBe(expectedSql);
        rendered.Parameters["@__gql_rls_0"].ShouldBe("EU%");
    }

    [Fact]
    public void BuildsTypedPredicate_ForIn_AndNull()
    {
        var inPredicate = _builder.Build([Filter("region", "IN", "[\"EU\",\"US\"]")], Orders);
        _renderer.Render(inPredicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql
            .ShouldBe("\"region\" IN (@__gql_rls_0, @__gql_rls_1)");

        _renderer.Render(_builder.Build([Filter("region", "EQ", "null")], Orders), TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql
            .ShouldBe("\"region\" IS NULL");
        _renderer.Render(_builder.Build([Filter("region", "NEQ", "null")], Orders), TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql
            .ShouldBe("\"region\" IS NOT NULL");
    }

    [Fact]
    public void EveryValue_IsPolicyParameter()
    {
        var predicate = _builder.Build(
        [
            Filter("region", "EQ", "\"EU\"", group: 0),
            Filter("amount", "GT", "100", group: 0),
            Filter("flag", "EQ", "true", group: 1),
            Filter("code", "IN", "[\"a\",\"b\",\"c\"]", group: 1)
        ], Orders);

        predicate.Parameters.Count.ShouldBe(6);
        predicate.Parameters.Values.Select(v => v.Type).ShouldBe(
            new[] { SqlParameterType.String, SqlParameterType.Int64, SqlParameterType.Boolean, SqlParameterType.String, SqlParameterType.String, SqlParameterType.String },
            ignoreOrder: true);

        // The AST holds no literal value other than the structural 1 = 0 / 1 = 1: only parameter nodes carry values.
        var rendered = _renderer.Render(predicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);
        rendered.Sql.ShouldNotContain("EU");
        rendered.Sql.ShouldNotContain("100");
        rendered.Sql.ShouldNotContain("'");
        rendered.Parameters.Count.ShouldBe(6);
    }

    [Fact]
    public void Groups_AreOrCombined_AndConditionsAndCombined()
    {
        var predicate = _builder.Build(
        [
            Filter("region", "EQ", "\"EU\"", group: 0),
            Filter("amount", "GT", "100", group: 0),
            Filter("region", "EQ", "\"US\"", group: 1)
        ], Orders);

        _renderer.Render(predicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql
            .ShouldBe("((\"region\" = @__gql_rls_0 AND \"amount\" > @__gql_rls_1) OR \"region\" = @__gql_rls_2)");
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, 3, 3)]
    [InlineData(TargetSqlDialect.PostgreSql, 5000, 5000)]
    public void InList_OneParameterPerItem(TargetSqlDialect dialect, int count, int expectedParameters)
    {
        var values = JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => $"v{i}"));
        var predicate = _builder.Build([Filter("region", "IN", values)], Orders);

        var rendered = _renderer.Render(predicate, dialect, RowFilterAliases.Target);
        rendered.Parameters.Count.ShouldBe(expectedParameters);
        rendered.Sql.ShouldNotContain("v1");
    }

    [Fact]
    public void InList_RespectsDialectLimit_OracleChunks()
    {
        var values = JsonSerializer.Serialize(Enumerable.Range(0, 2500).Select(i => $"v{i}"));
        var predicate = _builder.Build([Filter("region", "IN", values)], Orders, TargetSqlDialect.Oracle);

        var rendered = _renderer.Render(predicate, TargetSqlDialect.Oracle, RowFilterAliases.Target);
        rendered.Parameters.Count.ShouldBe(2500);
        // ORA-01795: no IN list may exceed 1000 items; three lists of 1000, 1000 and 500 joined by OR.
        System.Text.RegularExpressions.Regex.Count(rendered.Sql, " IN \\(").ShouldBe(3);
    }

    [Fact]
    public void UserAttribute_FailsClosed_WithPolarity()
    {
        var userAttribute = new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueJson = "\"x\"", ValueSource = "USER_ATTRIBUTE" };
        var a = new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Allow, RowFilters = [userAttribute] };
        var d = new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Deny, RowFilters = [userAttribute] };

        _renderer.Render(_builder.BuildCombined([a], [], Orders)!, TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql.ShouldBe("1 = 0");
        _renderer.Render(_builder.BuildCombined([], [d], Orders)!, TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql.ShouldBe("NOT (1 = 1)");
    }

    [Theory]
    [InlineData("reg ion")]
    [InlineData("a\"; DROP TABLE x;--")]
    public void InvalidColumnName_Throws(string column)
    {
        Should.Throw<InvalidOperationException>(() => _builder.Build([Filter(column, "EQ", "\"x\"")], Orders));
    }

    [Fact]
    public void UnsupportedOperator_Throws()
    {
        Should.Throw<InvalidOperationException>(() => _builder.Build([Filter("region", "REGEX", "\"x\"")], Orders));
    }

    [Fact]
    public void Build_WithoutFilters_Throws()
    {
        Should.Throw<ArgumentException>(() => _builder.Build([], Orders));
    }

    [Fact]
    public void RowFilterSqlBuilder_Parameterized_RendersFromTheTypedPredicate()
    {
        var sut = new RowFilterSqlBuilder();
        var a = new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Allow, RowFilters = [Filter("region", "IN", "[\"EU\",\"US\"]"), Filter("amount", "GT", "5")] };

        var typed = _builder.BuildCombined([a], [], Orders)!;
        var rendered = _renderer.Render(typed, TargetSqlDialect.SqlServer, RowFilterAliases.Target);
        var legacy = sut.BuildCombinedRowFilterParameterized([a], [], DatabaseDialect.SqlServer);

        legacy.Sql.ShouldBe(rendered.Sql);
        legacy.Parameters.ShouldBe(rendered.Parameters);
    }

    [Fact]
    public void Renderer_RebasesTheTargetAlias()
    {
        var filter = new Autheris.Domain.Model.VirtualFilter
        {
            TenantId = new Autheris.Domain.Common.TenantId("t"),
            Name = "f",
            Source = "erp",
            Sql = "from main.customers c where c.id = target.customer_id"
        };
        var predicate = Autheris.Application.VirtualFilters.SqlFilterCompiler.CompileTyped(
            filter, DatabaseDialect.PostgreSql, TrinoSqlEngine.FastSqlEngine.Default);

        _renderer.Render(predicate, TargetSqlDialect.PostgreSql, "o").Sql.ShouldContain("\"o\".\"customer_id\"");
        _renderer.Render(predicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql.ShouldContain("\"autheris_target\".\"customer_id\"");
    }

    [Fact]
    public void Renderer_RejectsAnUnsafeAliasAndAnUnknownDialect()
    {
        var predicate = _builder.Build([Filter("region", "EQ", "\"EU\"")], Orders);

        Should.Throw<ArgumentException>(() => _renderer.Render(predicate, TargetSqlDialect.PostgreSql, "a\"; --"));
        Should.Throw<ArgumentOutOfRangeException>(() => _renderer.Render(predicate, (TargetSqlDialect)999, RowFilterAliases.Target));
    }
}
