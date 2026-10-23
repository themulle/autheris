namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>VF-01: a SQL virtual filter definition is one flat query with unambiguous qualifiers, so no inner alias can shadow a tenant conjunct.</summary>
[Trait("Category", "Security")]
public sealed class SqlFilterCompilerNestedQueryTests
{
    private static TableMetadata Table(string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, table),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = table, SourceType = nameof(DatabaseDialect.PostgreSql), IsActive = true },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static VirtualFilter SqlFilter(string sql) => new()
    {
        TenantId = new TenantId("tenant_lwe"),
        Name = "nested_sql",
        Source = "lwetem_prod",
        Sql = sql
    };

    private static IReadOnlyList<TableMetadata> Catalog(bool craneTenanted = false) =>
    [
        Table("conf", "client", "id", "client_id", "tenant_id"),
        craneTenanted ? Table("md", "crane", "id", "client_id", "tenant_id") : Table("md", "crane", "id", "client_id")
    ];

    private static VirtualFilterTenantContext Context() =>
        new("tenant_a", id => id.TableName == "client" ? "tenant_id" : null);

    private const string Prefix = "from conf.client client where client.tenant_id = '{{tenant}}' and ";

    [Fact]
    public void Validate_InSubqueryReusingOuterAlias_Rejected() =>
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(
            Prefix + "target.client_id in (select client.client_id from conf.client client where client.tenant_id = 'tenant_b')"), Catalog()));

    [Fact]
    public void Validate_ExistsSubquery_Rejected() =>
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(
            Prefix + "target.client_id = client.client_id and exists (select 1 from md.crane crane where crane.client_id = client.client_id)"), Catalog()));

    [Fact]
    public void Validate_ScalarSubquery_Rejected() =>
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(
            Prefix + "target.client_id = (select max(crane.client_id) from md.crane crane)"), Catalog()));

    [Fact]
    public void Validate_DerivedTableInFrom_Rejected() =>
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(
            "from (select * from conf.client) client where client.tenant_id = '{{tenant}}' and target.client_id = client.client_id"), Catalog()));

    [Fact]
    public void Validate_SelfJoinWithoutAliases_Rejected() =>
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(
            "from conf.client join conf.client on conf.client.id = conf.client.id where client.tenant_id = '{{tenant}}' and target.client_id = client.client_id"), Catalog()));

    [Fact]
    public void Validate_AliasEqualsOtherTableName_Rejected() =>
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(
            "from conf.client c join md.crane client on client.client_id = c.client_id where c.tenant_id = '{{tenant}}' and target.client_id = c.client_id"), Catalog()));

    [Fact]
    public void Compile_StoredDefinitionWithSubquery_ThrowsInvalidOperation() =>
        Should.Throw<InvalidOperationException>(() => SqlFilterCompiler.Compile(SqlFilter(
            Prefix + "target.client_id in (select client.client_id from conf.client client where client.tenant_id = 'tenant_b')"),
            DatabaseDialect.PostgreSql, Context()));

    [Fact]
    public void Validate_JoinWithBothTenantConjuncts_Accepted() =>
        SqlFilterCompiler.Validate(SqlFilter(
            "from conf.client client join md.crane crane on crane.client_id = client.client_id " +
            "where client.tenant_id = '{{tenant}}' and crane.tenant_id = '{{tenant}}' and target.client_id = client.client_id"), Catalog(craneTenanted: true)).ShouldNotBeNull();
}
