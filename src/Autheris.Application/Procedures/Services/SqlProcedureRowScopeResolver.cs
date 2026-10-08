namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Evaluates the consent row filter of a procedure's result table in the database: for the keys found in the procedure
/// result it runs <c>SELECT keys FROM table AS autheris_target WHERE tenant AND (row filter) AND (key tuples)</c> in
/// batches and returns the keys that survive.
/// <list type="bullet">
/// <item>The lookup uses the governed read connection of the table's data source (the connection every governed table
/// read uses), never the EXECUTE-only procedure login of ADR-018.</item>
/// <item>The key must be unique in the table (primary key or a unique, unfiltered index), verified in the database per
/// dialect; otherwise one allowed row would admit every result row with the same key.</item>
/// <item>SQL Server, PostgreSQL and SQLite; the dialect of the connection must match the catalog dialect the row filter
/// was generated for.</item>
/// </list>
/// </summary>
public sealed class SqlProcedureRowScopeResolver : IProcedureRowScopeResolver
{
    private static readonly TimeSpan UniquenessCacheTtl = TimeSpan.FromMinutes(5);

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<SqlProcedureRowScopeResolver>? _logger;
    private readonly IDbSessionContextInitializer _sessionInitializer;
    private readonly ConcurrentDictionary<string, (bool Unique, DateTimeOffset CheckedAt)> _uniquenessCache = new(StringComparer.Ordinal);

    public SqlProcedureRowScopeResolver(
        ISqlConnectionFactory connectionFactory,
        IOptions<GatewayOptions> options,
        ILogger<SqlProcedureRowScopeResolver>? logger = null,
        IDbSessionContextInitializer? sessionInitializer = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
    }

    public async Task<IReadOnlySet<string>> GetAllowedKeysAsync(
        ProcedureDefinition definition,
        TableMetadata table,
        TableAccessDecision decision,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<object?[]> candidateKeys,
        ProcedureSecurityContext security,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(candidateKeys);

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (keyColumns.Count == 0 || candidateKeys.Count == 0)
        {
            return allowed;
        }

        // The governed read connection of the table (same rule as SqlConnector): the table's data source, else the
        // procedure's catalog domain.
        string connectionName = !string.IsNullOrWhiteSpace(table.Table.SourceName) ? table.Table.SourceName : definition.CatalogDomain;
        var connections = _options.Value.DataSources?.Connections;
        if (connections == null || !connections.TryGetValue(connectionName, out var connOptions) || string.IsNullOrWhiteSpace(connOptions.ConnectionString))
        {
            throw new InvalidOperationException($"No governed read connection is configured for data source '{connectionName}'.");
        }

        if (!ProcedureConnectionProvider.TryResolveDialect(connOptions.Provider, out var dialect) ||
            dialect is not (DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite))
        {
            throw new InvalidOperationException("Row scope filtering of procedure results supports SQL Server, PostgreSQL and SQLite only.");
        }

        // The row filter was generated with the catalog dialect; another backend would read it differently.
        if (dialect != table.Dialect)
        {
            throw new InvalidOperationException($"Catalog dialect {table.Dialect} of '{table.Identifier}' does not match the data source dialect {dialect}.");
        }

        string? filterSql = decision.CombinedRowFilterSql;
        if (!string.IsNullOrWhiteSpace(filterSql))
        {
            Autheris.Application.Sql.SqlSecurityValidator.ValidatePredicateSql(filterSql, "CombinedRowFilterSql");
        }

        int filterParameterCount = decision.RowFilterParameters?.Count ?? 0;
        int keyBudget = MaxParameters(dialect) - filterParameterCount - 1;
        if (keyBudget < keyColumns.Count)
        {
            throw new InvalidOperationException("The row filter uses too many parameters for a row scope lookup.");
        }

        int tuplesPerBatch = Math.Min(1000, keyBudget / keyColumns.Count);

        // Same tenant isolation as every governed table read.
        var tenantColumn = TableMetadata.RequireTenantColumnOrThrow(
            table,
            _options.Value.DataSources?.RequireTenantColumn == true,
            _options.Value.DataSources?.TenantColumnExemptTables);

        int timeout = Math.Max(1, Math.Min(definition.TimeoutSeconds, _options.Value.SqlEndpoints.Procedures.MaxTimeoutSeconds));

        // Distinct tuples only (the procedure may return several rows per key).
        var tuples = new List<object?[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in candidateKeys)
        {
            string? normalized = RowScopeKeys.Normalize(key);
            if (normalized != null && seen.Add(normalized))
            {
                tuples.Add(key);
            }
        }

        if (tuples.Count == 0)
        {
            return allowed;
        }

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);

        await EnsureUniqueKeyAsync(connection, dialect, connectionName, table, keyColumns, timeout, ct).ConfigureAwait(false);

        // PostgreSQL: transaction-local settings need a transaction; the lookup is read-only and is never committed.
        await using DbTransaction? tx = dialect == DatabaseDialect.PostgreSql
            ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

        if (tx != null)
        {
            // R-SQL-4: the lookup must not change data, also not through a function in a consent filter. SET TRANSACTION
            // must precede every query of the transaction, so it runs before the session initializer.
            await using var readOnly = connection.CreateCommand();
            readOnly.Transaction = tx;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await _sessionInitializer.InitializeSessionAsync(
            connection,
            tx,
            dialect,
            new TenantId(security.TenantId),
            userSid: security.UserSid,
            purpose: security.Purpose,
            ct: ct).ConfigureAwait(false);

        for (int offset = 0; offset < tuples.Count; offset += tuplesPerBatch)
        {
            var batch = tuples.Skip(offset).Take(tuplesPerBatch).ToList();

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandType = CommandType.Text;
            cmd.CommandTimeout = timeout;
            cmd.CommandText = BuildKeyQuery(dialect, table, keyColumns, tenantColumn, filterSql, batch.Count);
            if (_options.Value.Logging?.LogGeneratedSql == true)
            {
                _logger?.LogDebug("SqlProcedureRowScopeResolver: Generated SQL: {Sql}", cmd.CommandText);
            }

            if (tenantColumn != null)
            {
                AddParameter(cmd, "@rs_tenant", security.TenantId);
            }

            if (!string.IsNullOrWhiteSpace(filterSql) && decision.RowFilterParameters != null)
            {
                foreach (var (pName, pVal) in decision.RowFilterParameters)
                {
                    AddParameter(cmd, pName, pVal);
                }
            }

            for (int i = 0; i < batch.Count; i++)
            {
                for (int k = 0; k < keyColumns.Count; k++)
                {
                    AddParameter(cmd, $"@rs_{i}_{k}", batch[i][k]);
                }
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var values = new object?[keyColumns.Count];
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                for (int k = 0; k < values.Length; k++)
                {
                    values[k] = reader.IsDBNull(k) ? null : reader.GetValue(k);
                }

                string? normalized = RowScopeKeys.Normalize(values);
                if (normalized != null)
                {
                    allowed.Add(normalized);
                }
            }
        }

        if (tx != null)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return allowed;
    }

    /// <summary>Parameter limits per command: SQL Server 2100, SQLite 999 on older builds, PostgreSQL 65535.</summary>
    internal static int MaxParameters(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.SqlServer => 2000,
        DatabaseDialect.Sqlite => 900,
        _ => 30000
    };

    /// <summary>
    /// <c>SELECT keys FROM table AS autheris_target WHERE tenant = @rs_tenant AND (filter) AND ((k0 = @rs_0_0 AND ...) OR ...)</c>.
    /// Identifiers are quoted per dialect; correlated row filters bind to the reserved alias.
    /// </summary>
    internal static string BuildKeyQuery(
        DatabaseDialect dialect,
        TableMetadata table,
        IReadOnlyList<string> keyColumns,
        string? tenantColumn,
        string? filterSql,
        int tupleCount)
    {
        string keyList = string.Join(", ", keyColumns.Select(c => dialect.QuoteIdentifier(c)));
        string from = dialect.FormatTableIdentifier(table.Identifier) + " AS " + dialect.QuoteIdentifier(TrinoSqlEngine.RowFilterAliases.Target);

        var where = new List<string>();
        if (tenantColumn != null)
        {
            where.Add($"{dialect.QuoteIdentifier(tenantColumn)} = @rs_tenant");
        }

        if (!string.IsNullOrWhiteSpace(filterSql))
        {
            where.Add($"({filterSql})");
        }

        var tupleSql = new StringBuilder();
        for (int i = 0; i < tupleCount; i++)
        {
            if (i > 0)
            {
                tupleSql.Append(" OR ");
            }

            tupleSql.Append('(');
            for (int k = 0; k < keyColumns.Count; k++)
            {
                if (k > 0)
                {
                    tupleSql.Append(" AND ");
                }

                tupleSql.Append(dialect.QuoteIdentifier(keyColumns[k])).Append(" = @rs_").Append(i).Append('_').Append(k);
            }

            tupleSql.Append(')');
        }

        where.Add($"({tupleSql})");
        return $"SELECT {keyList} FROM {from} WHERE {string.Join(" AND ", where)}";
    }

    /// <summary>
    /// Catalog query that lists the key columns of every primary key and unique, unfiltered, enabled index of the
    /// table (one row per index and column). Parameter <c>@rs_table</c>.
    /// </summary>
    internal static string BuildUniqueIndexQuery(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.SqlServer =>
            "SELECT CAST(i.index_id AS bigint), c.name FROM sys.indexes i " +
            "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 " +
            "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            "WHERE i.object_id = OBJECT_ID(@rs_table) AND (i.is_unique = 1 OR i.is_primary_key = 1) AND i.has_filter = 0 AND i.is_disabled = 0 AND i.is_hypothetical = 0",
        DatabaseDialect.PostgreSql =>
            "SELECT i.indexrelid::bigint, a.attname::text FROM pg_index i " +
            "CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) " +
            "JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum " +
            "WHERE i.indrelid = to_regclass(@rs_table) AND i.indisunique AND i.indisvalid AND i.indpred IS NULL " +
            "AND i.indexprs IS NULL AND k.ord <= i.indnkeyatts",
        DatabaseDialect.Sqlite =>
            // The primary key (including INTEGER PRIMARY KEY, which has no index entry) is reported as index id -1.
            // An expression part of an index has no column name; it becomes '' so that index can never match a key.
            "SELECT -1, name FROM pragma_table_info(@rs_table) WHERE pk > 0 " +
            "UNION ALL SELECT il.seq, COALESCE(ii.name, '') FROM pragma_index_list(@rs_table) AS il " +
            "JOIN pragma_index_info(il.name) AS ii WHERE il.\"unique\" = 1 AND il.partial = 0",
        _ => throw new InvalidOperationException($"Dialect {dialect} is not supported for row scope filtering.")
    };

    /// <summary>The table argument of <see cref="BuildUniqueIndexQuery"/> per dialect.</summary>
    internal static string UniqueIndexTableArgument(DatabaseDialect dialect, TableIdentifier table) => dialect switch
    {
        // OBJECT_ID / to_regclass parse a (quoted) qualified name; the pragma functions take the bare table name.
        DatabaseDialect.Sqlite => table.TableName,
        _ => dialect.FormatTableIdentifier(table)
    };

    /// <summary>
    /// True when one of the indexes has exactly the key columns. PostgreSQL compares names exactly (case-sensitive
    /// identifiers), SQL Server and SQLite case-insensitively.
    /// </summary>
    internal static bool HasUniqueIndexOn(DatabaseDialect dialect, IEnumerable<(long IndexId, string Column)> indexColumns, IReadOnlyList<string> keyColumns)
    {
        var comparer = dialect == DatabaseDialect.PostgreSql ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var key = new HashSet<string>(keyColumns, comparer);
        return indexColumns
            .GroupBy(ic => ic.IndexId)
            .Any(index => new HashSet<string>(index.Select(ic => ic.Column), comparer).SetEquals(key));
    }

    private async Task EnsureUniqueKeyAsync(
        DbConnection connection,
        DatabaseDialect dialect,
        string connectionName,
        TableMetadata table,
        IReadOnlyList<string> keyColumns,
        int timeout,
        CancellationToken ct)
    {
        string cacheKey = $"{connectionName}|{table.Identifier}|{string.Join(",", keyColumns.Select(k => k.ToLowerInvariant()))}";
        if (_uniquenessCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CheckedAt < UniquenessCacheTtl)
        {
            if (!cached.Unique)
            {
                throw NotUnique(table, keyColumns);
            }

            return;
        }

        var indexColumns = new List<(long IndexId, string Column)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandType = CommandType.Text;
            cmd.CommandTimeout = timeout;
            cmd.CommandText = BuildUniqueIndexQuery(dialect);
            AddParameter(cmd, "@rs_table", UniqueIndexTableArgument(dialect, table.Identifier));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                indexColumns.Add((Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(1)));
            }
        }

        bool unique = HasUniqueIndexOn(dialect, indexColumns, keyColumns);
        _uniquenessCache[cacheKey] = (unique, DateTimeOffset.UtcNow);
        if (!unique)
        {
            throw NotUnique(table, keyColumns);
        }
    }

    private static InvalidOperationException NotUnique(TableMetadata table, IReadOnlyList<string> keyColumns) =>
        new($"row_scope_key ({string.Join(", ", keyColumns)}) is not a primary key or unique index of '{table.Identifier}'.");

    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
