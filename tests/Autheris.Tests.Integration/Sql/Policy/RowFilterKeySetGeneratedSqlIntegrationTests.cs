namespace Autheris.Tests.Integration.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Sql.Policy;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issue #107 WP-B (SEC-RF107-06 f): PostgreSQL execution parity between correlated <c>EXISTS</c> and key-set
/// substitution for <see cref="CorrelatedKeySetSubstitution"/> and <see cref="PolicyKeyQueryCompiler"/>.
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetGeneratedSqlIntegrationTests
{
    private static readonly TableIdentity SAir1 = new("main", "air1");
    private static readonly TenantBinding Tenant = new("__autheris_tenant", "acme", SqlParameterType.String);
    private static readonly FastSqlEngine Compiler = new();
    private readonly TestCorrelatedPredicateBuilder _builder = new();

    private static InMemoryTableCatalog PgCatalog() => new(
    [
        new TableCatalogEntry(SAir1, ImmutableArray.Create(
            new CatalogColumn("id", "integer"), new CatalogColumn("client_id", "integer"), new CatalogColumn("region", "text"), new CatalogColumn("tenant_id", "text")), "tenant_id", 1),
        new TableCatalogEntry(new TableIdentity("main", "client"), ImmutableArray.Create(
            new CatalogColumn("id", "integer"), new CatalogColumn("country", "text"), new CatalogColumn("tenant_id", "text")), "tenant_id", 1),
        new TableCatalogEntry(new TableIdentity("main", "crane"), ImmutableArray.Create(
            new CatalogColumn("id", "integer"), new CatalogColumn("client_id", "integer"), new CatalogColumn("kind", "text"), new CatalogColumn("tenant_id", "text")), "tenant_id", 1)
    ], "main");

    private static TestCorrelatedRule PgExample1(string kind, int group = 0) => new()
    {
        FilterGroup = group,
        IsCorrelated = true,
        DependentTable = new TableIdentifier("ds", "main", "client"),
        DependentTableAlias = "c",
        ForeignKeyColumn = "client_id",
        PrimaryKeyColumn = "id",
        Hop = (new TableIdentifier("ds", "main", "crane"), "cr", "c.id", "cr.client_id"),
        WhereColumn = "cr.kind",
        WhereValue = kind
    };

    private static TestCorrelatedRule Simple(string column = "region", string value = "\"US\"") => TestCorrelatedRule.Simple(column, value);
    private static TestConsent Allow(params TestCorrelatedRule[] filters) => new(ConsentEffect.Allow, filters);
    private static TestConsent Deny(params TestCorrelatedRule[] filters) => new(ConsentEffect.Deny, filters);

    private PolicyPredicate PgPolicy(TestConsent[] allow, TestConsent[]? deny = null) => _builder.BuildCombined(allow, deny ?? [], SAir1)!;

    private static PolicyKeyQueryRequest KeyRequest(ExistsExpression exists, PolicyPredicate predicate, ITableCatalog catalog, TableIdentity target, int limit = 2001) =>
        new(exists, predicate.Parameters, catalog, catalog.Resolve(target.ToQualifiedName())!, Tenant, TargetSqlDialect.PostgreSql, limit);

    private static CompiledSql Compile(string sql, ITableCatalog catalog, TableIdentity table, PolicyPredicate predicate)
    {
        var provider = new Policies(new Dictionary<TableIdentity, PolicyPredicate> { [table] = predicate });
        return Compiler.Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy { RowFilters = provider, Masks = provider, Catalog = catalog, Tenant = Tenant },
            MaxExpansionFactor = CompileLimits.MaxExpansionFactorLimit
        }, CancellationToken.None);
    }

    private static async Task<NpgsqlConnection> SeedAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS main;
            CREATE TABLE main.air1 (id integer, client_id integer NULL, region text, tenant_id text);
            CREATE TABLE main.client (id integer, country text, tenant_id text);
            CREATE TABLE main.crane (id integer, client_id integer, kind text, tenant_id text);
            INSERT INTO main.client VALUES (1,'CH','acme'), (2,'DE','acme'), (3,'CH','other'), (4,'CH','acme');
            INSERT INTO main.crane VALUES (10,1,'crane','acme'), (11,2,'tower','acme'), (12,3,'crane','other'), (13,4,'crane','acme'), (14,2,'crane','other');
            INSERT INTO main.air1 VALUES (1,1,'EU','acme'), (2,2,'EU','acme'), (3,NULL,'EU','acme'), (4,3,'EU','acme'),
                                         (5,4,'US','acme'), (6,99,'US','acme'), (7,NULL,'US','acme'), (8,1,'EU','other');
            """;
        await cmd.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<List<long>> RunAsync(NpgsqlConnection connection, CompiledSql compiled)
    {
        await using var command = connection.CreateCommand();
        new PostgreSqlCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>());

        var rows = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        rows.Sort();
        return rows;
    }

    private static async Task<(List<long> Keys, CompiledKeyQuery Key)> LoadKeysAsync(
        NpgsqlConnection connection, ExistsExpression node, PolicyPredicate predicate, ITableCatalog catalog)
    {
        var key = ((IPolicyKeyQueryCompiler)Compiler).CompileKeyQuery(KeyRequest(node, predicate, catalog, SAir1), CancellationToken.None);
        var keys = await RunAsync(connection, new CompiledSql(key.Sql, key.Parameters, TargetSqlDialect.PostgreSql, SqlStatementClass.Select, [], key.CompilerVersion));
        return (keys, key);
    }

    private static async Task<(List<long> Exists, List<long> KeySet)> ParityAsync(NpgsqlConnection connection, PolicyPredicate predicate, int expectedSites)
    {
        var catalog = PgCatalog();
        const string sql = "SELECT id FROM main.air1";
        var viaExists = await RunAsync(connection, Compile(sql, catalog, SAir1, predicate));

        var sites = CorrelatedKeySetSubstitution.FindSites(predicate);
        sites.Count.ShouldBe(expectedSites);
        var map = new List<(ExistsExpression, KeySetReplacement)>();
        for (int i = 0; i < sites.Count; i++)
        {
            var (keys, key) = await LoadKeysAsync(connection, sites[i].Node, predicate, catalog);
            map.Add((sites[i].Node, new KeySetReplacement(key.ForeignKeyColumn,
                keys.Select(k => new PolicyValue(k, SqlParameterType.Int64)).ToList(), KeySetPadding.Bucket(keys.Count, 2000), i)));
        }

        var substituted = CorrelatedKeySetSubstitution.Substitute(predicate, map, TargetSqlDialect.PostgreSql);
        var viaKeys = await RunAsync(connection, Compile(sql, catalog, SAir1, substituted));
        return (viaExists, viaKeys);
    }

    [RequiresContainerRuntimeFact]
    public async Task KeyQuery_ReturnsTheDistinctTenantScopedKeys_WithoutNull()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var connection = await SeedAsync(db.ConnectionString);
        var predicate = PgPolicy([Allow(PgExample1("crane"))]);

        var (keys, key) = await LoadKeysAsync(connection, CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node, predicate, PgCatalog());

        keys.ShouldBe([1L, 4L]);
        key.ForeignKeyColumn.ShouldBe("client_id");
    }

    [RequiresContainerRuntimeFact]
    public async Task Parity_SingleSite_WithNullForeignKeys()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var connection = await SeedAsync(db.ConnectionString);
        var (viaExists, viaKeys) = await ParityAsync(connection, PgPolicy([Allow(PgExample1("crane"))]), 1);

        viaExists.ShouldBe([1L, 5L]);
        viaKeys.ShouldBe(viaExists);
    }

    [RequiresContainerRuntimeFact]
    public async Task Parity_AllowOrSimpleFilter()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var connection = await SeedAsync(db.ConnectionString);
        var (viaExists, viaKeys) = await ParityAsync(connection, PgPolicy([Allow(PgExample1("crane")), Allow(Simple("region", "\"US\""))]), 1);

        viaExists.ShouldBe([1L, 5L, 6L, 7L]);
        viaKeys.ShouldBe(viaExists);
    }

    [RequiresContainerRuntimeFact]
    public async Task Parity_DenyConsentOnTheSameTable_StaysExists_AndKeepsNullForeignKeyRows()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var connection = await SeedAsync(db.ConnectionString);
        var predicate = PgPolicy([Allow(PgExample1("crane")), Allow(Simple("region", "\"EU\""))], [Deny(PgExample1("tower"))]);
        var (viaExists, viaKeys) = await ParityAsync(connection, predicate, 1);

        viaExists.ShouldBe([1L, 3L, 4L, 5L]);
        viaKeys.ShouldBe(viaExists);
    }

    [RequiresContainerRuntimeFact]
    public async Task Parity_EmptyKeySet_IsOneEqualsZero()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var connection = await SeedAsync(db.ConnectionString);
        var alone = await ParityAsync(connection, PgPolicy([Allow(PgExample1("nonexistent"))]), 1);
        var orSimple = await ParityAsync(connection, PgPolicy([Allow(PgExample1("nonexistent")), Allow(Simple("region", "\"US\""))]), 1);

        alone.Exists.ShouldBeEmpty();
        alone.KeySet.ShouldBeEmpty();
        orSimple.Exists.ShouldBe([5L, 6L, 7L]);
        orSimple.KeySet.ShouldBe(orSimple.Exists);
    }

    [RequiresContainerRuntimeFact]
    public async Task Parity_AndGroup_OfASiteAndASimpleFilter()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var connection = await SeedAsync(db.ConnectionString);
        var (viaExists, viaKeys) = await ParityAsync(connection, PgPolicy([Allow(PgExample1("crane", 0), Simple("region", "\"EU\""))]), 1);

        viaExists.ShouldBe([1L]);
        viaKeys.ShouldBe(viaExists);
    }

    private sealed class Policies(Dictionary<TableIdentity, PolicyPredicate> predicates) : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => predicates[table];
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }
}
