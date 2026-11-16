namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>
/// Pure in-memory fixture replacing the temp-file SQLite key source for unit tests of caching,
/// guardrails, concurrency, and error handling without opening any database engine.
/// </summary>
internal sealed class KeySourceStubFixture
{
    internal const string Domain = "lwetem_prod";

    internal static readonly TenantId TenantA = new("tenant_lwe");
    internal static readonly TenantId TenantB = new("tenant_other");

    public bool Cleared { get; set; }

    public KeySourceStubFixture()
    {
        Catalog = Substitute.For<ITableMetadataRepository>();
        foreach (var table in Tables)
        {
            Catalog.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>()).Returns(table);
        }
    }

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
        Table = new Table { SourceName = Domain, SchemaName = schema, TableName = table, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
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
            Connections = { [Domain] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = connectionString ?? "Host=stub;Database=vf_keys" } }
        },
        VirtualFilters = new VirtualFilterOptions { Federation = new VirtualFilterFederationOptions { MaxAuthorizedKeys = maxKeys } }
    };

    internal ISqlConnectionFactory CreateFactory() => new StubKeySourceConnectionFactory(this);

    internal SqlVirtualFilterKeySetProvider Provider(GatewayOptions? options = null, ISqlConnectionFactory? factory = null, VirtualFilterKeySetCache? cache = null)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(options ?? Options());
        return new SqlVirtualFilterKeySetProvider(Catalog, factory ?? CreateFactory(), opts, cache ?? new VirtualFilterKeySetCache(opts));
    }

    private sealed class StubKeySourceConnectionFactory(KeySourceStubFixture owner) : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            if (options.ConnectionString.Contains("/nonexistent_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"unable to open database file {options.ConnectionString}");
            }

            DbConnection conn = new CallbackDbConnection((sql, _) =>
            {
                if (sql.Contains("\"many\"", StringComparison.Ordinal))
                {
                    return new StubDbDataReader(["k"], [[1L], [2L], [3L], [4L], [5L]]);
                }

                if (sql.Contains("\"is_delivered\" IS NOT NULL", StringComparison.OrdinalIgnoreCase))
                {
                    return new StubDbDataReader(["client_id"], [[1L], [2L]]);
                }

                IReadOnlyList<object?[]> rows = owner.Cleared ? [] : [[3L], [4L], [5L]];
                return new StubDbDataReader(["client_id"], rows);
            });
            return Task.FromResult(conn);
        }
    }
}

/// <summary>Counts key-source connections; can pause deterministically via a barrier or fail them to test single-flight loading and failure caching.</summary>
internal sealed class CountingConnectionFactory(ISqlConnectionFactory inner) : ISqlConnectionFactory
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public TaskCompletionSource? Entered { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public int FailNext { get; set; }

    public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        if (Gate is { } gate)
        {
            Entered?.TrySetResult();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("database said: secret-value-42 is invalid");
        }

        return await inner.CreateOpenConnectionAsync(options, ct).ConfigureAwait(false);
    }
}
