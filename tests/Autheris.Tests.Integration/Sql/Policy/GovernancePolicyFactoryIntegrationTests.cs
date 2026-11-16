namespace Autheris.Tests.Integration.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Sql.Policy;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// PostgreSQL execution verification of policies assembled by <see cref="GovernancePolicyFactory"/>.
/// </summary>
public sealed class GovernancePolicyFactoryIntegrationTests
{
    private static readonly TableIdentifier OrdersId = new("d", "main", "orders");
    private static readonly TableIdentity Orders = new("main", "orders");
    private static readonly TableIdentity Invoices = new("main", "invoices");
    private static readonly PolicyPredicateRenderer Renderer = new();
    private readonly FastSqlEngine _engine = new();

    private static TableCatalogEntry Entry(TableIdentity id, string tenantType = "text") => new(id, ImmutableArray.Create(
        new CatalogColumn("id", "integer"), new CatalogColumn("tenant_id", tenantType), new CatalogColumn("region", "text"),
        new CatalogColumn("email", "text")), "tenant_id", 1);

    private static ITableCatalog Catalog(params TableCatalogEntry[] entries) => new InMemoryTableCatalog(entries, "main");

    private static GovernancePolicyFactory Factory(ITableCatalog catalog, out RecordingCatalogProvider provider)
    {
        provider = new RecordingCatalogProvider(catalog);
        return new GovernancePolicyFactory(provider, new StubTableMetadataRepository(), Options.Create(new GatewayOptions()));
    }

    private sealed class RecordingCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public List<(GovernedDataSource DataSource, IReadOnlyCollection<TableIdentifier> Tables)> Calls { get; } = [];

        public ValueTask<ITableCatalog> GetCatalogAsync(
            GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
        {
            Calls.Add((dataSource, tables));
            return new ValueTask<ITableCatalog>(catalog);
        }
    }

    private sealed class StubTableMetadataRepository : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Table = new Table { SourceName = "ds", SchemaName = table.Schema, TableName = table.TableName, IsActive = true },
                Identifier = table,
                Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "tenant_id" }]
            });

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private static IRowPolicy RegionPolicy(string region = "EU")
    {
        var namer = new PolicyParameterNamer("__gql_rls_");
        var parameter = namer.Add(new PolicyValue(region, SqlParameterType.String));
        var expression = new BinaryExpression(PolicyParameterNamer.Column("region"), BinaryOperator.Equal, parameter);
        return new PolicyPredicateRowPolicy(PolicyPredicate.Create(expression, namer.Values), TargetSqlDialect.PostgreSql, Renderer);
    }

    private static TableAccessDecision Allowed(TableIdentifier table, IRowPolicy? rowPolicy = null, ColumnAccessLevel email = ColumnAccessLevel.Clear, IColumnMaskPolicy? masks = null)
    {
        var decision = TableAccessDecision.Allowed(table,
            new Dictionary<string, ColumnAccessLevel> { ["email"] = email }, hasUnconstrainedColumnAllow: true);
        if (rowPolicy != null)
        {
            decision = decision.WithAdditionalRowPolicy(rowPolicy);
        }

        return masks is null ? decision : decision with { ColumnMasks = masks };
    }

    private static ColumnMaskPolicy EmailMask() => new(new Dictionary<string, MaskSpec>
    {
        ["email"] = SqlDataMaskingProvider.BuildUnkeyedMaskSpec(null, "email", DatabaseDialect.PostgreSql)
    });

    private static GovernedPolicyInput Input(
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions, IEnumerable<TableIdentity>? withoutRls = null, string tenant = "acme") =>
        new("ds", new TenantId(tenant), decisions, (withoutRls ?? []).ToHashSet(), GovernedSubqueryStrategy.Correlated);

    private static CompileRequest Request(GovernancePolicy policy) =>
        CompileRequestFactory.Create(policy, TargetSqlDialect.PostgreSql, new WebSqlOptions(), false, 100, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private async Task<CompileRequest> AssembleAsync(
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions, IEnumerable<TableIdentity>? withoutRls = null)
    {
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out _);
        return Request(await factory.CreateAsync(Input(decisions, withoutRls), CancellationToken.None));
    }

    private async Task<List<object?[]>> RunAsync(string connectionString, CompileRequest request, string sql)
    {
        var compiled = _engine.Compile(sql.AsMemory(), request, CancellationToken.None);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE SCHEMA IF NOT EXISTS main;
                DROP TABLE IF EXISTS main.orders, main.invoices;
                CREATE TABLE main.orders (id integer, tenant_id text, region text, email text);
                CREATE TABLE main.invoices (id integer, tenant_id text, region text, email text);
                INSERT INTO main.orders VALUES (1, 'acme', 'EU', 'a@x'), (2, 'acme', 'US', 'b@x'), (3, 'other', 'EU', 'c@x'), (4, 'ACME', 'EU', 'd@x');
                INSERT INTO main.invoices VALUES (1, 'acme', 'EU', 'z@x');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        new PostgreSqlCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>());
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    private static List<int> Ids(List<object?[]> rows) =>
        rows.Select(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture)).OrderBy(x => x).ToList();

    [RequiresContainerRuntimeFact]
    public async Task TypedRowPolicy_FiltersRows_AndIsBound()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy("EU")) });
        Ids(await RunAsync(db.ConnectionString, request, "SELECT id FROM orders")).ShouldBe([1]);
    }

    [RequiresContainerRuntimeFact]
    public async Task TypedMask_IsApplied()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = Allowed(OrdersId, RegionPolicy("EU"), email: ColumnAccessLevel.Mask, masks: EmailMask())
        });
        var rows = await RunAsync(db.ConnectionString, request, "SELECT id, email FROM orders");
        rows.Count.ShouldBe(1);
        rows[0][1].ShouldBe("***");
    }

    [RequiresContainerRuntimeFact]
    public async Task DeniedColumn_IsNullified()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = Allowed(OrdersId, RegionPolicy("EU"), email: ColumnAccessLevel.Deny)
        });
        var rows = await RunAsync(db.ConnectionString, request, "SELECT id, email FROM orders");
        rows.Count.ShouldBe(1);
        rows[0][1].ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task NoRowPolicy_DeclaredWithoutRls_ReturnsTenantRows()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var request = await AssembleAsync(
            new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId) }, withoutRls: [Orders]);
        Ids(await RunAsync(db.ConnectionString, request, "SELECT id FROM orders")).ShouldBe([1, 2]);
    }

    [RequiresContainerRuntimeFact]
    public async Task NoRowPolicy_NotDeclaredWithoutRls_DeniesRows()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId) });
        (await RunAsync(db.ConnectionString, request, "SELECT id FROM orders")).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task MandatoryPredicate_WithMatchingTypedPolicy_IsEnforced()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var decision = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithMandatoryPredicate(RegionPolicy("US"), ["vf"]);
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = decision });
        Ids(await RunAsync(db.ConnectionString, request, "SELECT id FROM orders")).ShouldBe([2]);
    }

    private static IRowPolicy CorrelatedPolicy() => new PolicyPredicateRowPolicy(
        PolicyPredicate.Create(new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null) },
                new NamedTableSource(new SqlQualifiedName([new SqlIdentifier("invoices")]), null),
                new BinaryExpression(
                    PolicyParameterNamer.Column("invoices.id"),
                    BinaryOperator.Equal,
                    PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id")),
                null, null),
            null, null)),
        new Dictionary<string, PolicyValue>()), TargetSqlDialect.PostgreSql, Renderer);

    [RequiresContainerRuntimeFact]
    public async Task PolicySubqueryTable_IsRequestedFromTheProvider_ResolvableForThePolicy_ButNeverAdmitted()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out var provider);
        var decisions = new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, CorrelatedPolicy()) };

        var policy = await factory.CreateAsync(Input(decisions), CancellationToken.None);

        provider.Calls.Any(c =>
            c.DataSource == (GovernedDataSource)"ds" && c.Tables.Any(x => x.TableName == "invoices")).ShouldBeTrue();
        var request = Request(policy);
        Ids(await RunAsync(db.ConnectionString, request, "SELECT id FROM orders ORDER BY id")).ShouldBe([1]);
        Ids(await RunAsync(db.ConnectionString, request, "SELECT id FROM invoices")).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task CorrelatedConsentFilter_BuiltByTheProducer_CompilesAndFiltersEndToEnd()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var predicate = TestCorrelatedPredicateBuilder.BuildSingle(
            fkColumn: "id",
            domain: "default",
            schema: "main",
            table: "invoices",
            alias: "dep",
            pkColumn: "id",
            whereColumn: null);
        var policy = new PolicyPredicateRowPolicy(predicate, TargetSqlDialect.PostgreSql, Renderer);
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out _);
        var gp = await factory.CreateAsync(Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, policy) }), CancellationToken.None);

        Ids(await RunAsync(db.ConnectionString, Request(gp), "SELECT id FROM orders ORDER BY id")).ShouldBe([1]);
    }
}
