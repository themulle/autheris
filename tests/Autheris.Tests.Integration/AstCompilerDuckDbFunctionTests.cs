namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using DuckDB.NET.Data;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-X0-fix item 3: <c>date_add</c> with <c>TranslateTrinoDateFunctions</c> is translated for DuckDB and executed on in-process DuckDB.
/// </summary>
public sealed class AstCompilerDuckDbFunctionTests : IDisposable
{
    private static readonly TableIdentity Orders = new("main", "Orders");

    private readonly DuckDBConnection _conn = new("DataSource=:memory:");
    private readonly DuckDbCompiledSqlBinder _binder = new();
    private readonly FastSqlEngine _engine = new();

    public AstCompilerDuckDbFunctionTests()
    {
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE "Orders" ("Id" INTEGER PRIMARY KEY, "TenantId" VARCHAR NOT NULL, "Email" VARCHAR);
            INSERT INTO "Orders" VALUES (1, 'acme', 'alice.smith@acme.example'), (2, 'other', 'carol@other.example');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    private List<int> Ids(string sql)
    {
        var compiled = _engine.Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = TargetSqlDialect.DuckDb,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            TranslateTrinoDateFunctions = true,
            Policy = new GovernancePolicy
            {
                RowFilters = new NoPolicies(),
                Masks = new NoPolicies(),
                Catalog = new InMemoryTableCatalog(new[]
                {
                    new TableCatalogEntry(Orders, ImmutableArray.Create(
                        new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "VARCHAR"), new CatalogColumn("Email", "VARCHAR")), "TenantId", 1)
                }, "main"),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }, CancellationToken.None);
        using var cmd = _conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        var ids = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(Convert.ToInt32(reader.GetValue(0)));
        return ids;
    }

    private sealed class NoPolicies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData("date_add('day', 1, current_timestamp) > current_timestamp")]
    [InlineData("date_add('day', -1, current_timestamp) < current_timestamp")]
    [InlineData("date_add('month', 1, current_timestamp) > date_add('day', 27, current_timestamp)")]
    [InlineData("date_add('month', 1, current_timestamp) < date_add('day', 32, current_timestamp)")]
    [InlineData("date_add('hour', 2, current_timestamp) > date_add('minute', 119, current_timestamp)")]
    [InlineData("date_add('week', 1, current_timestamp) > date_add('day', 6, current_timestamp)")]
    [InlineData("date_add('week', 1, current_timestamp) < date_add('day', 8, current_timestamp)")]
    public void DateAdd_TranslatedAndExecuted(string predicate) =>
        Ids($"SELECT id FROM orders WHERE id = 1 AND {predicate}").ShouldBe(new List<int> { 1 });

    [Fact]
    public void DateAdd_TheTranslationIsNotAConstantTrue() =>
        Ids("SELECT id FROM orders WHERE id = 1 AND date_add('day', 1, current_timestamp) < current_timestamp").ShouldBeEmpty();
}
