namespace Autheris.Tests.Unit.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>WP-SIMPL-48 PR 1: Typed named parameters on row policies.</summary>
public sealed class RowPolicyParameterTests
{
    private static readonly TenantId Tenant = new("tenant_lwe");

    private static TableMetadata OrdersTable() => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", "sales", "orders"),
        Table = new Table
        {
            SourceName = "lwetem_prod",
            SchemaName = "sales",
            TableName = "orders",
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql
        },
        Columns =
        [
            new TableColumn { ColumnName = "client_id", DataType = "integer" },
            new TableColumn { ColumnName = "region", DataType = "varchar" }
        ]
    };

    private static VirtualFilter StructuredParamFilter(IReadOnlyList<RowPolicyParameter> parameters, string whereVal = ":region") => new()
    {
        TenantId = Tenant,
        Name = "region_filter",
        Source = "lwetem_prod",
        KeyColumns = ["client.client_id"],
        Parameters = parameters,
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("lwetem_prod", "conf", "client"),
            FromAlias = "client",
            Where = [new FilterCondition("client.region", FilterConditionOperator.Eq, whereVal)]
        }
    };

    private static VirtualFilter SqlParamFilter(IReadOnlyList<RowPolicyParameter> parameters, string sql) => new()
    {
        TenantId = Tenant,
        Name = "sql_param_filter",
        Source = "lwetem_prod",
        Parameters = parameters,
        Sql = sql
    };

    private static JsonElement JsonOf(object? value) =>
        JsonSerializer.SerializeToElement(value);

    [Fact]
    [Trait("Category", "Security")]
    public void Parameter_values_always_bound_never_inlined()
    {
        var injection = "EU' OR '1'='1";
        var structured = StructuredParamFilter([new RowPolicyParameter("region", RowPolicyParameterType.String)]);
        var sqlFilter = SqlParamFilter(
            [
                new RowPolicyParameter("region", RowPolicyParameterType.String),
                new RowPolicyParameter("ids", RowPolicyParameterType.Int64, IsList: true, MaxValues: 5)
            ],
            "from conf.client client where target.client_id = client.client_id and client.region = :region and client.client_id in (:ids)");

        var builder = new StructuredFilterSqlBuilder();
        var binding = new FilterBinding { FilterName = "region_filter" };
        var values = new Dictionary<string, JsonElement>
        {
            ["region"] = JsonOf(injection),
            ["ids"] = JsonOf(new long[] { 42L, 99L })
        };

        var structuredPredicate = builder.BuildTyped(
            structured,
            binding,
            OrdersTable(),
            DatabaseDialect.PostgreSql,
            "__p_",
            tenant: null,
            new Dictionary<string, JsonElement> { ["region"] = JsonOf(injection) });
        var sqlPredicate = SqlFilterCompiler.CompileTyped(
            sqlFilter,
            DatabaseDialect.PostgreSql,
            FastSqlEngine.Default,
            "__p_",
            tenant: null,
            values);

        var renderer = new PolicyPredicateRenderer();
        var renderedStructured = renderer.Render(structuredPredicate!, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);
        var renderedSql = renderer.Render(sqlPredicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);

        renderedStructured.Sql.ShouldNotContain("EU");
        renderedStructured.Sql.ShouldNotContain("1=1");
        renderedStructured.Parameters.Values.ShouldContain(injection);

        renderedSql.Sql.ShouldNotContain("EU");
        renderedSql.Sql.ShouldNotContain("42");
        renderedSql.Sql.ShouldNotContain("99");
        renderedSql.Parameters.Values.ShouldContain(injection);
        renderedSql.Parameters.Values.ShouldContain(42L);
        renderedSql.Parameters.Values.ShouldContain(99L);

        // Text-only paths never inline parameter values
        builder.Build(structured, binding, OrdersTable(), DatabaseDialect.PostgreSql).ShouldBe(SqlFilterCompiler.CallerAttributeFilterText);
        SqlFilterCompiler.Compile(sqlFilter, DatabaseDialect.PostgreSql).ShouldBe(SqlFilterCompiler.CallerAttributeFilterText);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Parameter_type_and_count_validated()
    {
        var filter = new VirtualFilter
        {
            TenantId = Tenant,
            Name = "typed_params",
            Source = "lwetem_prod",
            Parameters =
            [
                new RowPolicyParameter("s", RowPolicyParameterType.String),
                new RowPolicyParameter("n", RowPolicyParameterType.Int64),
                new RowPolicyParameter("d", RowPolicyParameterType.Decimal),
                new RowPolicyParameter("g", RowPolicyParameterType.Guid),
                new RowPolicyParameter("dt", RowPolicyParameterType.Date),
                new RowPolicyParameter("list", RowPolicyParameterType.Int64, IsList: true, MaxValues: 2)
            ]
        };

        var valid = new Dictionary<string, JsonElement>
        {
            ["s"] = JsonOf("ok"),
            ["n"] = JsonOf(123L),
            ["d"] = JsonOf(45.67m),
            ["g"] = JsonOf(Guid.Parse("11111111-2222-3333-4444-555555555555").ToString()),
            ["dt"] = JsonOf("2026-11-15"),
            ["list"] = JsonOf(new long[] { 10L, 20L })
        };

        var bound = RowPolicyParameterBinder.BindValues(filter, valid);
        bound["n"][0].Value.ShouldBe(123L);
        bound["list"].Count.ShouldBe(2);

        void AssertInvalid(string key, object? badValue)
        {
            var copy = new Dictionary<string, JsonElement>(valid) { [key] = JsonOf(badValue) };
            var ex = Should.Throw<VirtualFilterBindingException>(() => RowPolicyParameterBinder.BindValues(filter, copy));
            ex.Code.ShouldBe(VirtualFilterErrorCodes.RowPolicyParameterInvalid);
        }

        AssertInvalid("n", "123"); // string rejected for Int64
        AssertInvalid("n", 12.5m); // non-integer rejected for Int64
        AssertInvalid("d", "not-a-decimal");
        AssertInvalid("g", "not-a-guid");
        AssertInvalid("dt", "15/11/2026"); // invalid ISO date
        AssertInvalid("s", new[] { "a" }); // array rejected for scalar
        AssertInvalid("list", 10L); // scalar rejected for list
        AssertInvalid("list", Array.Empty<long>()); // empty list rejected
        AssertInvalid("list", new long[] { 1L, 2L, 3L }); // exceeds MaxValues=2
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unknown_parameter_rejected()
    {
        var undeclaredStructured = StructuredParamFilter([], ":secret");
        Should.Throw<VirtualFilterBindingException>(() => RowPolicyParameterBinder.ValidateFilterDeclarations(undeclaredStructured))
            .Code.ShouldBe(VirtualFilterErrorCodes.RowPolicyParameterInvalid);

        var undeclaredSql = SqlParamFilter(
            [new RowPolicyParameter("region", RowPolicyParameterType.String)],
            "from conf.client client where target.client_id = client.client_id and client.region = :other");
        Should.Throw<VirtualFilterBindingException>(() => RowPolicyParameterBinder.ValidateFilterDeclarations(undeclaredSql))
            .Code.ShouldBe(VirtualFilterErrorCodes.RowPolicyParameterInvalid);

        var filter = StructuredParamFilter([new RowPolicyParameter("region", RowPolicyParameterType.String)]);
        var extra = new Dictionary<string, JsonElement>
        {
            ["region"] = JsonOf("EU"),
            ["unexpected"] = JsonOf("x")
        };
        Should.Throw<VirtualFilterBindingException>(() => RowPolicyParameterBinder.BindValues(filter, extra))
            .Code.ShouldBe(VirtualFilterErrorCodes.RowPolicyParameterInvalid);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Invalid_parameter_at_decision_denies()
    {
        var filter = StructuredParamFilter([new RowPolicyParameter("region", RowPolicyParameterType.String)]);
        var sqlFilter = SqlParamFilter(
            [new RowPolicyParameter("limit_id", RowPolicyParameterType.Int64)],
            "from conf.client client where target.client_id = client.client_id and client.client_id = :limit_id");
        var builder = new StructuredFilterSqlBuilder();
        var binding = new FilterBinding { FilterName = filter.Name };

        // Missing required parameters deny on compile/evaluation
        Should.Throw<VirtualFilterBindingException>(() =>
            builder.BuildTyped(filter, binding, OrdersTable(), DatabaseDialect.PostgreSql, "__p_", null, null))
            .Code.ShouldBe(VirtualFilterErrorCodes.RowPolicyParameterInvalid);

        // Mistyped runtime parameter denies on evaluation
        var badSqlValues = new Dictionary<string, JsonElement> { ["limit_id"] = JsonOf("wrong-type") };
        Should.Throw<VirtualFilterBindingException>(() =>
            SqlFilterCompiler.CompileTyped(sqlFilter, DatabaseDialect.PostgreSql, FastSqlEngine.Default, "__p_", null, badSqlValues))
            .Code.ShouldBe(VirtualFilterErrorCodes.RowPolicyParameterInvalid);
    }
}
