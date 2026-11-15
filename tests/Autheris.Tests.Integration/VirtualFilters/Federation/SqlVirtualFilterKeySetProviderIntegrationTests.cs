namespace Autheris.Tests.Integration.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.VirtualFilters;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;

/// <summary>F-GOV-14 WI-5: PostgreSQL execution of structured key-source queries (joins, NULL/NotEq/duplicates, composite keys, tenant isolation).</summary>
public sealed class SqlVirtualFilterKeySetProviderIntegrationTests
{
    private const string Domain = "lwetem_prod";
    private static readonly TenantId TenantA = new("tenant_lwe");
    private static readonly TenantId TenantB = new("tenant_other");
    private static readonly IReadOnlyList<VirtualFilterKeyType> IntegerKey = [VirtualFilterKeyType.IntegerNumber];

    private static IReadOnlyList<TableMetadata> Tables { get; } =
    [
        Sql("conf", "client", "client_id", "crane_serial_number"),
        Sql("md", "crane", "serial_number", "is_delivered"),
        Sql("auth", "assignment", "client_id", "crane_serial_number", "status", "revoked_at"),
        Sql("fms", "air1", "id", "client_id"),
        Sql("auth", "grant_t", "client_id", "tenant_id")
    ];

    private static TableMetadata Sql(string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(Domain, schema, table),
        Table = new Table { SourceName = Domain, SchemaName = schema, TableName = table, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "integer" }).ToList()
    };

    private static VirtualFilter Filter(string name, string fromSchema, string fromTable, string alias, string key, params FilterCondition[] where) => new()
    {
        TenantId = TenantA,
        Name = name,
        Source = Domain,
        KeyColumns = [$"{alias}.{key}"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier(Domain, fromSchema, fromTable),
            FromAlias = alias,
            Where = where
        }
    };

    private static VirtualFilter David() => VirtualFilterTestData.DavidFilter() with { TenantId = TenantA };

    private static VirtualFilter Parity() => Filter("parity", "auth", "assignment", "a", "client_id") with
    {
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier(Domain, "auth", "assignment"),
            FromAlias = "a",
            Joins = [new FilterJoin(new TableIdentifier(Domain, "md", "crane"), "crane", "crane.serial_number", "a.crane_serial_number")],
            Where =
            [
                new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNull),
                new FilterCondition("a.revoked_at", FilterConditionOperator.IsNull),
                new FilterCondition("a.status", FilterConditionOperator.NotEq, "INACTIVE")
            ]
        }
    };

    private static long[] Values(VirtualFilterKeySet set) => set.Keys.Select(k => (long)k[0]).OrderBy(v => v).ToArray();

    private static async Task<NpgsqlConnection> SeedAsync(PostgreSqlTestDatabase db)
    {
        var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA conf;
            CREATE SCHEMA md;
            CREATE SCHEMA auth;
            CREATE SCHEMA fms;

            CREATE TABLE md.crane (serial_number TEXT PRIMARY KEY, is_delivered INTEGER);
            CREATE TABLE conf.client (client_id INTEGER PRIMARY KEY, crane_serial_number TEXT);
            INSERT INTO md.crane VALUES ('C1', 1), ('C2', 1), ('C3', NULL), ('C4', NULL), ('C5', NULL);
            INSERT INTO conf.client VALUES (1, 'C1'), (2, 'C2'), (3, 'C3'), (4, 'C4'), (5, 'C5'), (6, 'NO-CRANE');

            CREATE TABLE auth.assignment (client_id TEXT, crane_serial_number TEXT, status TEXT, revoked_at TEXT);
            INSERT INTO auth.assignment VALUES
                ('3', 'C3', 'ACTIVE', NULL), ('3', 'C3', 'ACTIVE', NULL), ('4', 'C4', 'INACTIVE', NULL),
                ('5', 'C5', 'ACTIVE', '2024-01-01'), (NULL, 'C3', 'ACTIVE', NULL), ('7', 'C3', 'ACTIVE', NULL),
                ('1', 'C1', 'ACTIVE', NULL), ('8', 'NO-CRANE', 'ACTIVE', NULL), ('x', 'C3', 'ACTIVE', NULL);

            CREATE TABLE fms.air1 (id SERIAL PRIMARY KEY, client_id TEXT);
            INSERT INTO fms.air1 (client_id) VALUES ('1'), ('3'), ('3'), ('4'), ('5'), ('7'), ('8'), (NULL), ('9');

            CREATE TABLE auth.grant_t (client_id INTEGER, tenant_id TEXT);
            INSERT INTO auth.grant_t VALUES (1, 'tenant_lwe'), (2, 'tenant_lwe'), (3, 'tenant_other'), (3, 'tenant_lwe');
            """;
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    private static SqlVirtualFilterKeySetProvider CreateProvider(
        string connectionString,
        bool requireTenantColumn = false,
        IEnumerable<string>? exempt = null)
    {
        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                RequireTenantColumn = requireTenantColumn,
                TenantColumnExemptTables = exempt?.ToList() ?? [],
                Connections = { [Domain] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = connectionString } }
            },
            VirtualFilters = new VirtualFilterOptions { Federation = new VirtualFilterFederationOptions { MaxAuthorizedKeys = 50_000 } }
        };
        var opts = Microsoft.Extensions.Options.Options.Create(gatewayOptions);
        return new SqlVirtualFilterKeySetProvider(
            new InMemoryTableRepo(Tables),
            new SqlConnectionFactory(new StaticHostEnvironment("Development")),
            opts,
            new VirtualFilterKeySetCache(opts));
    }

    private static async Task<List<object?>> QueryAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var values = new List<object?>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
        }

        return values;
    }

    [RequiresContainerRuntimeFact]
    public async Task Get_DavidCaseJoinAndIsNull_ReturnsExactKeys()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await using var _ = await SeedAsync(db);

        var set = await CreateProvider(db.ConnectionString).GetAsync(David(), IntegerKey, TenantA);

        Values(set).ShouldBe([3L, 4L, 5L]);
        set.CacheHit.ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public async Task Get_KeySetParity_WithSqlChannelPredicate()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await using var conn = await SeedAsync(db);

        var filter = Parity();
        var binding = new FilterBinding { FilterName = "parity" };
        var air1 = Tables.Single(t => t.Identifier.TableName == "air1");
        var predicate = new StructuredFilterSqlBuilder().Build(filter, binding, air1, DatabaseDialect.PostgreSql);
        var sqlChannel = (await QueryAsync(conn, $"SELECT DISTINCT \"client_id\" FROM \"fms\".\"air1\" AS \"autheris_target\" WHERE {predicate}"))
            .Select(v => Convert.ToInt64(v)).OrderBy(v => v).ToArray();

        var set = await CreateProvider(db.ConnectionString).GetAsync(filter, IntegerKey, TenantA);

        var authorizedAir1Keys = (await QueryAsync(conn, "SELECT DISTINCT client_id FROM fms.air1 WHERE client_id IS NOT NULL"))
            .Select(v => Convert.ToInt64(v)).Where(v => Values(set).Contains(v)).OrderBy(v => v).ToArray();
        sqlChannel.ShouldBe([3L, 7L]);
        authorizedAir1Keys.ShouldBe(sqlChannel);
        Values(set).ShouldBe([3L, 7L]);
        set.DroppedKeyCount.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Get_NotEqCondition_Excludes()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await using var _ = await SeedAsync(db);

        var filter = Filter("not_inactive", "auth", "assignment", "a", "client_id",
            new FilterCondition("a.status", FilterConditionOperator.NotEq, "INACTIVE"));

        var set = await CreateProvider(db.ConnectionString).GetAsync(filter, [VirtualFilterKeyType.Text], TenantA);

        set.Keys.Select(k => (string)k[0]).ShouldNotContain("4");
        set.Keys.Select(k => (string)k[0]).ShouldContain("3");
    }

    [RequiresContainerRuntimeFact]
    public async Task Get_NullKeysExcluded_DuplicatesDeduplicated()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await using var _ = await SeedAsync(db);

        var filter = Filter("all_assignments", "auth", "assignment", "a", "client_id");

        var set = await CreateProvider(db.ConnectionString).GetAsync(filter, [VirtualFilterKeyType.Text], TenantA);

        set.Keys.Select(k => (string)k[0]).OrderBy(v => v, StringComparer.Ordinal).ShouldBe(["1", "3", "4", "5", "7", "8", "x"]);
        set.DroppedKeyCount.ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task Get_CompositeKey()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await using var _ = await SeedAsync(db);

        var filter = Filter("composite", "auth", "assignment", "a", "client_id",
            new FilterCondition("a.status", FilterConditionOperator.Eq, "ACTIVE")) with
        { KeyColumns = ["a.client_id", "a.crane_serial_number"] };

        var set = await CreateProvider(db.ConnectionString).GetAsync(filter, [VirtualFilterKeyType.Text, VirtualFilterKeyType.Text], TenantA);

        set.Keys.Select(k => $"{k[0]}/{k[1]}").OrderBy(v => v, StringComparer.Ordinal)
            .ShouldBe(["1/C1", "3/C3", "5/C5", "7/C3", "8/NO-CRANE", "x/C3"]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Get_TenantColumn_ReturnsOnlyTheCallersKeys()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await using var _ = await SeedAsync(db);

        var filter = Filter("granted", "auth", "grant_t", "g", "client_id");
        Tables.Single(t => t.Identifier.TableName == "grant_t").TenantColumnName.ShouldBe("tenant_id");

        var provider = CreateProvider(db.ConnectionString);
        var forA = await provider.GetAsync(filter, IntegerKey, TenantA);
        var forB = await provider.GetAsync(filter with { TenantId = TenantB }, IntegerKey, TenantB);

        Values(forA).ShouldBe([1L, 2L, 3L]);
        Values(forB).ShouldBe([3L]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Get_RequireTenantColumn_ExemptTables_Succeeds()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await using var _ = await SeedAsync(db);

        var provider = CreateProvider(db.ConnectionString, requireTenantColumn: true, exempt: ["conf.client", "crane"]);
        var set = await provider.GetAsync(David(), IntegerKey, TenantA);

        Values(set).ShouldBe([3L, 4L, 5L]);
    }

    private sealed class InMemoryTableRepo(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class StaticHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Autheris.Tests.Integration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
