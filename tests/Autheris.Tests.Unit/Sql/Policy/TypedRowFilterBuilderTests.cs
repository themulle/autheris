using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
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
    [InlineData("LIKE", "\"region\" LIKE @__gql_rls_0")]
    public void BuildsTypedPredicate_ForEveryOperator(string op, string expectedSql)
    {
        var predicate = _builder.Build([Filter("region", op, "\"EU\"")], Orders);

        predicate.ReferencedColumns.ShouldContain("region");
        var rendered = _renderer.Render(predicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target);
        rendered.Sql.ShouldBe(expectedSql);
        rendered.Parameters["@__gql_rls_0"].ShouldBe("EU");
    }

    [Fact]
    public void BuildsTypedPredicate_ForIn_AndNull()
    {
        var inPredicate = _builder.Build([Filter("region", "IN", "[\"EU\",\"US\"]")], Orders);
        _renderer.Render(inPredicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql
            .ShouldBe("\"region\" IN (@__gql_rls_0, @__gql_rls_1)");

        _renderer.Render(_builder.Build([Filter("region", "EQ", "null")], Orders), TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql
            .ShouldBe("\"region\" IS NULL");
        _renderer.Render(_builder.Build([Filter("region", "NEQ", "null")], Orders), TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql
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
        var rendered = _renderer.Render(predicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target);
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

        _renderer.Render(predicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql
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
        var a = new Consent { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Allow, RowFilters = [userAttribute] };
        var d = new Consent { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Deny, RowFilters = [userAttribute] };

        _renderer.Render(_builder.BuildCombined([a], [], Orders)!, TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql.ShouldBe("1 = 0");
        _renderer.Render(_builder.BuildCombined([], [d], Orders)!, TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql.ShouldBe("NOT (1 = 1)");
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
        var a = new Consent { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Allow, RowFilters = [Filter("region", "IN", "[\"EU\",\"US\"]"), Filter("amount", "GT", "5")] };

        var typed = _builder.BuildCombined([a], [], Orders)!;
        var rendered = _renderer.Render(typed, TargetSqlDialect.SqlServer, RowFilterAliases.Target);
        var legacy = sut.BuildCombinedRowFilterParameterized([a], [], DatabaseDialect.SqlServer);

        legacy.Sql.ShouldBe(rendered.Sql);
        legacy.Parameters.ShouldBe(rendered.Parameters);
    }

    [Fact]
    public void RenderedString_EqualsLegacyString_Semantically()
    {
        var a1 = new Consent { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Allow, RowFilters = [Filter("region", "IN", "[\"EU\",\"US\"]", 0), Filter("amount", "GTE", "100", 0), Filter("region", "EQ", "\"APAC\"", 1)] };
        var d1 = new Consent { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Deny, RowFilters = [Filter("secret", "EQ", "true")] };
        var sut = new RowFilterSqlBuilder();

        var typed = _builder.BuildCombined([a1], [d1], Orders)!;
        var rendered = _renderer.Render(typed, TargetSqlDialect.Sqlite, RowFilterAliases.Target);
        var legacySql = sut.BuildCombinedRowFilter([a1], [d1], DatabaseDialect.Sqlite)!;

        using var connection = SeedOrders();
        var viaTyped = Query(connection, $"SELECT id FROM orders AS autheris_target WHERE {rendered.Sql} ORDER BY id", rendered.Parameters);
        var viaLegacy = Query(connection, $"SELECT id FROM orders AS autheris_target WHERE {legacySql} ORDER BY id", new Dictionary<string, object?>());

        viaTyped.ShouldBe(viaLegacy);
        viaTyped.ShouldBe([1L, 3L, 5L]);
    }

    [Theory]
    [InlineData("it's \"quoted\"")]
    [InlineData("x' OR '1'='1")]
    [InlineData("'; DROP TABLE orders; --")]
    [InlineData("back\\slash %_ [x]")]
    [InlineData("line\nbreak\u0000nul")]
    [InlineData("ünï©ödé 日本語 😀")]
    public void HostileValue_RoundTripsExactly(string hostile)
    {
        var json = JsonSerializer.Serialize(hostile);
        var predicate = _builder.Build([Filter("region", "EQ", json)], Orders);
        var rendered = _renderer.Render(predicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target);

        rendered.Sql.ShouldNotContain(hostile.Length > 3 ? hostile[..4] : hostile);
        rendered.Parameters.Single().Value.ShouldBe(hostile);

        using var connection = SeedOrders();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO orders (id, region, amount, secret) VALUES (99, $r, 1, 0)";
            insert.Parameters.AddWithValue("$r", hostile);
            insert.ExecuteNonQuery();
        }

        // The tables still exist and exactly the hostile row matches.
        Query(connection, $"SELECT id FROM orders AS autheris_target WHERE {rendered.Sql}", rendered.Parameters).ShouldBe([99L]);
    }

    [Fact]
    public void Renderer_RebasesTheTargetAlias()
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("erp", "main", "customers"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id"
        };
        var predicate = AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(filter);

        _renderer.Render(predicate, TargetSqlDialect.Sqlite, "o").Sql.ShouldContain("\"o\".\"customer_id\"");
        _renderer.Render(predicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql.ShouldContain("\"autheris_target\".\"customer_id\"");
    }

    [Fact]
    public void Renderer_RejectsAnUnsafeAliasAndAnUnknownDialect()
    {
        var predicate = _builder.Build([Filter("region", "EQ", "\"EU\"")], Orders);

        Should.Throw<ArgumentException>(() => _renderer.Render(predicate, TargetSqlDialect.Sqlite, "a\"; --"));
        Should.Throw<ArgumentOutOfRangeException>(() => _renderer.Render(predicate, (TargetSqlDialect)999, RowFilterAliases.Target));
    }

    private static SqliteConnection SeedOrders()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE orders (id INTEGER PRIMARY KEY, region TEXT, amount INTEGER, secret INTEGER);
INSERT INTO orders VALUES (1, 'EU', 150, 0), (2, 'EU', 50, 0), (3, 'APAC', 1, 0), (4, 'US', 500, 1), (5, 'US', 100, 0), (6, 'DE', 999, 0);";
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static List<long> Query(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }
}
