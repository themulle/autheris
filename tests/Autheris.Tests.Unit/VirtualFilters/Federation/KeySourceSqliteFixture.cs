namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>
/// A real SQLite key source in a temp file (F-GOV-14 WI-5): the David case of <c>RowFilterChannelParityTests</c>
/// (client JOIN crane WHERE is_delivered IS NULL) plus tables for revocation, duplicates, NULL keys and tenants.
/// </summary>
internal sealed class KeySourceSqliteFixture : IDisposable
{
    internal const string Domain = "lwetem_prod";

    internal static readonly TenantId TenantA = new("tenant_lwe");
    internal static readonly TenantId TenantB = new("tenant_other");

    private const string Schema = """
        CREATE TABLE crane (serial_number TEXT PRIMARY KEY, is_delivered INTEGER);
        CREATE TABLE client (client_id INTEGER PRIMARY KEY, crane_serial_number TEXT);
        INSERT INTO crane VALUES ('C1', 1), ('C2', 1), ('C3', NULL), ('C4', NULL), ('C5', NULL);
        INSERT INTO client VALUES (1, 'C1'), (2, 'C2'), (3, 'C3'), (4, 'C4'), (5, 'C5'), (6, 'NO-CRANE');

        CREATE TABLE assignment (client_id INTEGER, crane_serial_number TEXT, status TEXT, revoked_at TEXT);
        INSERT INTO assignment VALUES
            (3, 'C3', 'ACTIVE', NULL), (3, 'C3', 'ACTIVE', NULL), (4, 'C4', 'INACTIVE', NULL),
            (5, 'C5', 'ACTIVE', '2024-01-01'), (NULL, 'C3', 'ACTIVE', NULL), (7, 'C3', 'ACTIVE', NULL),
            (1, 'C1', 'ACTIVE', NULL), (8, 'NO-CRANE', 'ACTIVE', NULL), ('x', 'C3', 'ACTIVE', NULL);

        CREATE TABLE air1 (id INTEGER PRIMARY KEY, client_id INTEGER);
        INSERT INTO air1 (client_id) VALUES (1), (3), (3), (4), (5), (7), (8), (NULL), (9);

        CREATE TABLE grant_t (client_id INTEGER, tenant_id TEXT);
        INSERT INTO grant_t VALUES (1, 'tenant_lwe'), (2, 'tenant_lwe'), (3, 'tenant_other'), (3, 'tenant_lwe');

        CREATE TABLE many (k INTEGER);
        INSERT INTO many VALUES (1), (2), (3), (4), (5), (5);
        """;

    private readonly string _path;

    public KeySourceSqliteFixture()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vf_keys_{Guid.NewGuid():N}.db");
        Execute(Schema);
        Catalog = Substitute.For<ITableMetadataRepository>();
        foreach (var table in Tables)
        {
            Catalog.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>()).Returns(table);
        }
    }

    public string ConnectionString => $"Data Source={_path}";

    public ITableMetadataRepository Catalog { get; }

    internal static IReadOnlyList<TableMetadata> Tables { get; } =
    [
        Sql("conf", "client", "client_id", "crane_serial_number"),
        Sql("md", "crane", "serial_number", "is_delivered"),
        Sql("auth", "assignment", "client_id", "crane_serial_number", "status", "revoked_at"),
        Sql("fms", "air1", "id", "client_id"),
        Sql("auth", "grant_t", "client_id", "tenant_id"),
        Sql("auth", "many", "k")
    ];

    internal static TableMetadata Sql(string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(Domain, schema, table),
        Table = new Table { SourceName = Domain, SchemaName = schema, TableName = table, SourceType = "Sqlite", DataSourceType = DataSourceType.Sql },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "integer" }).ToList()
    };

    internal static VirtualFilter Filter(string name, string fromSchema, string fromTable, string alias, string key, params FilterCondition[] where) => new()
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

    /// <summary>assignment JOIN crane WHERE delivered IS NULL AND revoked_at IS NULL AND status &lt;&gt; 'INACTIVE'.</summary>
    internal static VirtualFilter Parity() => Filter("parity", "auth", "assignment", "a", "client_id") with
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

    /// <summary>One stable instance: the key set cache is keyed by the filter id (a new id is a new filter).</summary>
    internal static VirtualFilter David() => DavidInstance;

    private static readonly VirtualFilter DavidInstance = VirtualFilterModelTests.DavidFilter() with { TenantId = TenantA };

    internal GatewayOptions Options(int maxKeys = 50_000, bool requireTenantColumn = false, IEnumerable<string>? exempt = null, string? connectionString = null) => new()
    {
        DataSources = new SqlDataSourceOptions
        {
            RequireTenantColumn = requireTenantColumn,
            TenantColumnExemptTables = exempt?.ToList() ?? [],
            Connections = { [Domain] = new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = connectionString ?? ConnectionString } }
        },
        VirtualFilters = new VirtualFilterOptions { Federation = new VirtualFilterFederationOptions { MaxAuthorizedKeys = maxKeys } }
    };

    internal SqlVirtualFilterKeySetProvider Provider(GatewayOptions? options = null, ISqlConnectionFactory? factory = null, VirtualFilterKeySetCache? cache = null)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(options ?? Options());
        return new SqlVirtualFilterKeySetProvider(Catalog, factory ?? new SqlConnectionFactory(), opts, cache ?? new VirtualFilterKeySetCache(opts));
    }

    internal void Execute(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal List<object?> Query(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<object?>();
        while (reader.Read())
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
        }

        return values;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

/// <summary>Counts key-source connections; can delay or fail them to test single-flight loading and failure caching.</summary>
internal sealed class CountingConnectionFactory(ISqlConnectionFactory inner) : ISqlConnectionFactory
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public TimeSpan Delay { get; set; }

    public int FailNext { get; set; }

    public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("database said: secret-value-42 is invalid");
        }

        return await inner.CreateOpenConnectionAsync(options, ct);
    }
}
