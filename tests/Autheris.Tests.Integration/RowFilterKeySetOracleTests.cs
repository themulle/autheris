namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issue #107 WP-C (WP-B hand-off (j), D-WPC-9 c): the target alias <c>autheris_target</c> is emitted unquoted on Oracle while the typed builders reference it
/// quoted in lower case. This test runs the correlated <c>EXISTS</c> form and the key-set <c>IN</c> form on Oracle Free and requires the same rows, so both
/// forms resolve the alias. If the <c>EXISTS</c> form failed too, it would be a pre-existing dialect generator defect, not a key-set special case.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed class RowFilterKeySetOracleTests
{
    private static readonly TableIdentity Orders = new(AstCompilerOracleFixture.AppUser, "ORDERS");
    private readonly AstCompilerOracleFixture _fx;

    public RowFilterKeySetOracleTests(AstCompilerOracleFixture fixture) => _fx = fixture;

    private DataSourceConnectionOptions Options() => new() { Provider = "Oracle", ConnectionString = _fx.AppConnectionString, DefaultSchema = AstCompilerOracleFixture.AppUser };

    private static InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("ID", "NUMBER(10)"), new CatalogColumn("TENANT_ID", "VARCHAR2(64)"), new CatalogColumn("REGION", "VARCHAR2(20)")), "TENANT_ID", 1),
        new TableCatalogEntry(new TableIdentity(AstCompilerOracleFixture.AppUser, "ENTITLEMENTS"), ImmutableArray.Create(
            new CatalogColumn("ID", "NUMBER(10)"), new CatalogColumn("TENANT_ID", "VARCHAR2(64)"), new CatalogColumn("ORDER_ID", "NUMBER(10)")), "TENANT_ID", 1)
    }, AstCompilerOracleFixture.AppUser);

    private sealed class Provider(PolicyPredicate predicate) : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => table == Orders;
        public PolicyPredicate GetPredicate(TableIdentity table) => table == Orders ? predicate : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private async Task<List<int>> RunAsync(PolicyPredicate predicate, string tenant)
    {
        var provider = new Provider(predicate);
        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.Oracle,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy { RowFilters = provider, Masks = provider, Catalog = Catalog(), Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String) }
        };
        var compiled = new FastSqlEngine().Compile($"SELECT ID FROM {AstCompilerOracleFixture.AppUser}.ORDERS".AsMemory(), request, CancellationToken.None);
        await using var conn = await new SqlConnectionFactory(new DevelopmentEnvironment()).CreateOpenConnectionAsync(Options());
        await new DbSessionContextInitializer().InitializeSessionAsync(conn, "Oracle", new TenantId("tenant-" + Guid.NewGuid().ToString("N")[..8]), requireTransaction: false);
        await using var cmd = conn.CreateCommand();
        new OracleCompiledSqlBinder().Bind(cmd, compiled, new Dictionary<string, object?>());
        var ids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(Convert.ToInt32(reader.GetValue(0)));
        ids.Sort();
        return ids;
    }

    // A READ ONLY transaction fails with ORA-01466 while a table's DDL is newer than the transaction snapshot;
    // fixture tables in this collection are (re)created just before, so retry until the DDL has settled.
    private static async Task<RowFilterKeySetEntry> LoadSettledAsync(RowFilterKeySetLoadRequest request)
    {
        var loader = new RowFilterKeySetLoader(new SqlConnectionFactory(new DevelopmentEnvironment()));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await loader.LoadAsync(request, CancellationToken.None);
            }
            catch (GatewaySecurityException ex) when (attempt < 10 && ex.InnerException is OracleException { Number: 1466 })
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public async Task TargetAliasSpelling_InListAndExistsResolve()
    {
        var filter = new ConsentRowFilter
        {
            Id = Guid.NewGuid(), FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("default", AstCompilerOracleFixture.AppUser, "ENTITLEMENTS"), DependentTableAlias = "e",
            ForeignKeyColumn = "ID", PrimaryKeyColumn = "ORDER_ID"
        };
        var exists = new TypedRowFilterBuilder().BuildCombined([new Consent { TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant, Effect = ConsentEffect.Allow, RowFilters = [filter] }], [], Orders)!;
        var node = exists.Expression switch
        {
            ExistsExpression e => e,
            ParenthesizedExpression { Expression: ExistsExpression e } => e,
            _ => throw new InvalidOperationException("expected a single correlated site")
        };
        var tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String);
        var catalog = Catalog();
        var query = new PolicyKeyQueryCompiler().CompileKeyQuery(
            new PolicyKeyQueryRequest(node, exists.Parameters, catalog, catalog.Resolve(Orders.ToQualifiedName())!, tenant, TargetSqlDialect.Oracle, 101), CancellationToken.None);
        var converter = KeySetValueConverter.Create(TargetSqlDialect.Oracle, query.KeyColumnType, query.ForeignKeyColumnType)!;
        var entry = await LoadSettledAsync(
            new RowFilterKeySetLoadRequest(Options(), TargetSqlDialect.Oracle, new TenantId("acme"), "ds", query, converter, 100, TimeSpan.FromSeconds(30)));
        entry.Keys.ShouldNotBeEmpty();

        var column = new ColumnReference(new SqlQualifiedName([new SqlIdentifier(RowFilterAliases.Target, true), new SqlIdentifier(query.ForeignKeyColumn, true)]));
        var values = new Dictionary<string, PolicyValue>();
        var items = new List<Expression>();
        for (int i = 0; i < entry.Keys.Count; i++)
        {
            string name = $"__rls_ks0_{i}";
            values[name] = new PolicyValue(entry.Keys[i], converter.ParameterType);
            items.Add(new PolicyParameterExpression(name, converter.ParameterType));
        }

        var inList = PolicyPredicate.Create(new InListExpression(column, items, false), values);

        var viaExists = await RunAsync(exists, "acme");
        var viaKeys = await RunAsync(inList, "acme");

        viaExists.ShouldBe([1, 6]);   // the acme orders with an acme entitlement
        viaKeys.ShouldBe(viaExists);
    }
}
