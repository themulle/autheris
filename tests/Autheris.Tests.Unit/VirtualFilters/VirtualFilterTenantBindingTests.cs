namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>SR-L-11: virtual filter predicates of the SQL channel restrict every tenanted source table to the caller's tenant.</summary>
[Trait("Category", "Security")]
public sealed class VirtualFilterTenantBindingTests
{
    private const string CallerTenant = "tenant_a";

    private static TableMetadata Table(string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, table),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = table, SourceType = nameof(DatabaseDialect.PostgreSql), IsActive = true },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static readonly TableMetadata Target = Table("fms", "air1", "id", "client_id");

    private static VirtualFilterTenantContext Context(bool clientTenanted = true, string column = "tenant_id") =>
        new(CallerTenant, id => id.TableName == "client" && clientTenanted ? column : null);

    private static VirtualFilter Structured() => VirtualFilterModelTests.DavidFilter();

    private static readonly FilterBinding Binding = new() { FilterName = "nicht_ausgelieferte_krane" };

    private static VirtualFilter SqlFilter(string sql) => new()
    {
        TenantId = new TenantId("tenant_lwe"),
        Name = "tenanted_sql",
        Source = "lwetem_prod",
        Sql = sql
    };

    private static IReadOnlyList<TableMetadata> Catalog(string tenantColumn = "tenant_id") =>
        [Table("conf", "client", "id", "client_id", tenantColumn), Table("md", "crane", "id")];

    [Fact]
    public void Build_TenantedSourceTable_EmitsTenantPredicateBoundToCallerTenant()
    {
        var sql = new StructuredFilterSqlBuilder().Build(Structured(), Binding, Target, DatabaseDialect.PostgreSql, Context());

        sql.ShouldContain("\"client\".\"tenant_id\" = 'tenant_a'");
        sql.ShouldNotContain("\"crane\".\"tenant_id\"");
    }

    [Fact]
    public void BuildTyped_TenantedSourceTable_BindsTenantAsParameter()
    {
        var typed = new StructuredFilterSqlBuilder().BuildTyped(Structured(), Binding, Target, DatabaseDialect.PostgreSql, "__gql_vf_", Context());

        typed.ShouldNotBeNull();
        var rendered = new PolicyPredicateRenderer().Render(typed, PolicyParameterNamer.ToTargetDialect(DatabaseDialect.PostgreSql), RowFilterAliases.Target);
        rendered.Sql.ShouldContain("\"client\".\"tenant_id\" = ");
        rendered.Sql.ShouldNotContain("tenant_a");
        rendered.Parameters.Values.ShouldContain(CallerTenant);
    }

    [Fact]
    public void Build_UntenantedSourceTable_NoPredicate()
    {
        var builder = new StructuredFilterSqlBuilder();
        var sql = builder.Build(Structured(), Binding, Target, DatabaseDialect.PostgreSql, Context(clientTenanted: false));

        sql.ShouldBe(builder.Build(Structured(), Binding, Target, DatabaseDialect.PostgreSql));
        sql.ShouldNotContain("tenant");
    }

    [Fact]
    public void Build_TenantColumnLookupFails_Throws()
    {
        var context = new VirtualFilterTenantContext(CallerTenant, _ => throw new InvalidOperationException("no tenant column"));

        Should.Throw<InvalidOperationException>(() => new StructuredFilterSqlBuilder().Build(Structured(), Binding, Target, DatabaseDialect.PostgreSql, context));
    }

    [Fact]
    public void Build_TenantValueWithQuote_IsEscaped()
    {
        var context = new VirtualFilterTenantContext("t'; DROP TABLE x; --", id => id.TableName == "client" ? "tenant_id" : null);

        var sql = new StructuredFilterSqlBuilder().Build(Structured(), Binding, Target, DatabaseDialect.PostgreSql, context);

        sql.ShouldContain("'t''; DROP TABLE x; --'");
    }

    private const string TenantBoundSql = """
        from conf.client client
        join md.crane crane on crane.serial_number = client.crane_serial_number
        where client.tenant_id = '{{tenant}}' and crane.is_delivered is null and target.client_id = client.client_id
        """;

    [Fact]
    public void Validate_TenantComparedWithReservedValue_Accepted()
    {
        SqlFilterCompiler.Validate(SqlFilter(TenantBoundSql), Catalog()).SqlTargetColumns.ShouldContain("client_id");
    }

    [Theory]
    [InlineData("from conf.client client where client.tenant_id is not null and target.client_id = client.client_id")]
    [InlineData("from conf.client client where client.tenant_id = 'tenant_b' and target.client_id = client.client_id")]
    [InlineData("from conf.client client where client.tenant_id = client.client_id and target.client_id = client.client_id")]
    [InlineData("from conf.client client where (client.tenant_id = '{{tenant}}' or client.client_id = 1) and target.client_id = client.client_id")]
    [InlineData("from conf.client client where target.client_id = client.client_id")]
    public void Validate_IsNotNullOnTenantColumn_Rejected(string sql)
    {
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(sql), Catalog())).Message.ShouldContain("tenant");
    }

    [Fact]
    public void Validate_CustomTenantColumnName_Required()
    {
        var sqlOnLiteralName = "from conf.client client where client.tenant_id = '{{tenant}}' and target.client_id = client.client_id";
        var sqlOnCustomName = "from conf.client client where client.TenantId = '{{tenant}}' and target.client_id = client.client_id";

        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(sqlOnLiteralName), Catalog("TenantId")));
        SqlFilterCompiler.Validate(SqlFilter(sqlOnCustomName), Catalog("TenantId")).ShouldNotBeNull();
    }

    [Fact]
    public void Validate_RequireTenantColumnAndMissing_Rejected()
    {
        var sql = "from conf.client client where target.client_id = client.client_id";
        var catalog = new[] { Table("conf", "client", "id", "client_id") };

        SqlFilterCompiler.Validate(SqlFilter(sql), catalog).ShouldNotBeNull();
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(sql), catalog, null, requireTenantColumn: true));
    }

    [Fact]
    public void Compile_BindsCallerTenant_AndDeniesStoredFilterWithoutTheReservedValue()
    {
        var bound = SqlFilterCompiler.Compile(SqlFilter(TenantBoundSql), DatabaseDialect.PostgreSql, Context());
        bound.ShouldContain("'tenant_a'");
        bound.ShouldNotContain("{{tenant}}");

        var legacy = SqlFilter("from conf.client client where client.tenant_id is not null and target.client_id = client.client_id");
        Should.Throw<InvalidOperationException>(() => SqlFilterCompiler.Compile(legacy, DatabaseDialect.PostgreSql, Context()));
    }

    [Fact]
    public void Compile_ReservedValueWithoutTenantContext_FailsClosed()
    {
        Should.Throw<InvalidOperationException>(() => SqlFilterCompiler.Compile(SqlFilter(TenantBoundSql), DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void Structured_And_Sql_Channels_AgreeOnTenantedTables()
    {
        var context = Context();
        var columns = context.ColumnsByAlias(Structured().Structured!);

        columns.Keys.ShouldBe(["client"]);
        columns["client"].ShouldBe("tenant_id");
    }
}
