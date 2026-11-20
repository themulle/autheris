namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Evaluates the consent row filter of a procedure's result table in the database: for the keys found in the procedure
/// result it runs a key-closure query <c>SELECT keys FROM (SELECT keys, CASE WHEN tenant AND (row filter) THEN 1 ELSE 0 END AS autheris_rs_ok FROM table AS autheris_target WHERE (key tuples)) AS autheris_rs GROUP BY keys HAVING COUNT(*) = SUM(autheris_rs_ok)</c>
/// in batches so a key tuple is admitted only when every row carrying that key satisfies both tenant isolation and the caller's row filter.
/// <list type="bullet">
/// <item>The lookup uses the governed read connection of the table's data source (the connection every governed table
/// read uses), never the EXECUTE-only procedure login of ADR-018.</item>
/// <item>SQL Server and PostgreSQL; the dialect of the connection must match the catalog dialect the row filter
/// was generated for.</item>
/// </list>
/// </summary>
public sealed class SqlProcedureRowScopeResolver : IProcedureRowScopeResolver
{
    internal const string RowScopeDerivedAlias = "autheris_rs";
    internal const string RowScopeFlagColumn = "autheris_rs_ok";

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<SqlProcedureRowScopeResolver>? _logger;
    private readonly IDbSessionContextInitializer _sessionInitializer;

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

        if (!DataSourceProvider.TryResolveDialect(connOptions.Provider, out var dialect) ||
            dialect is not (DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql))
        {
            throw new InvalidOperationException("Row scope filtering of procedure results supports SQL Server and PostgreSQL only.");
        }

        // The row filter was generated with the catalog dialect; another backend would read it differently.
        if (dialect != table.Dialect)
        {
            throw new InvalidOperationException($"Catalog dialect {table.Dialect} of '{table.Identifier}' does not match the data source dialect {dialect}.");
        }

        string? filterSql = decision.CombinedRowFilterSql;
        if (!string.IsNullOrWhiteSpace(filterSql))
        {
            Autheris.Application.Sql.SqlSecurityValidator.ValidateRowFilter(decision);
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
            bool allowStatementText = _options.Value.Logging?.AllowStatementText ?? true;
            if (_options.Value.Logging?.LogGeneratedSql == true && allowStatementText && _logger != null && _logger.IsEnabled(LogLevel.Debug))
            {
                if (SqlTextScrubber.TryScrub(cmd.CommandText, dialect, out var scrubbed, out var queryHash))
                {
                    _logger.LogDebug("SqlProcedureRowScopeResolver: Executing scope discovery query [{QueryHash}]: {StatementText}", queryHash, scrubbed);
                }
            }

            // a duplicate bare parameter name (two filter values, a filter value that names the tenant or a key) fails closed.
            var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tenantColumn != null)
            {
                AddParameter(cmd, "@rs_tenant", security.TenantId, bound);
            }

            if (!string.IsNullOrWhiteSpace(filterSql) && decision.RowFilterParameters != null)
            {
                foreach (var (pName, pVal) in decision.RowFilterParameters)
                {
                    AddParameter(cmd, pName, pVal, bound);
                }
            }

            for (int i = 0; i < batch.Count; i++)
            {
                for (int k = 0; k < keyColumns.Count; k++)
                {
                    AddParameter(cmd, $"@rs_{i}_{k}", batch[i][k], bound);
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

    /// <summary>Parameter limits per command: SQL Server 2100, PostgreSQL 65535.</summary>
    internal static int MaxParameters(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.SqlServer => 2000,
        _ => 30000
    };

    /// <summary>
    /// Key-closure query:
    /// <c>SELECT keys FROM (SELECT keys, CASE WHEN tenant = @rs_tenant AND (filter) THEN 1 ELSE 0 END AS autheris_rs_ok FROM table AS autheris_target WHERE ((k0 = @rs_0_0 AND ...) OR ...)) AS autheris_rs GROUP BY keys HAVING COUNT(*) = SUM(autheris_rs_ok)</c>.
    /// Identifiers are quoted per dialect; correlated row filters bind to the reserved alias, and the aggregate evaluates only the projected flag column.
    /// </summary>
    internal static string BuildKeyQuery(
        DatabaseDialect dialect,
        TableMetadata table,
        IReadOnlyList<string> keyColumns,
        string? tenantColumn,
        string? filterSql,
        int tupleCount)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keyColumns);
        if (keyColumns.Count == 0)
        {
            throw new ArgumentException("At least one key column is required.", nameof(keyColumns));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tupleCount);

        foreach (string keyColumn in keyColumns)
        {
            if (string.Equals(keyColumn, RowScopeFlagColumn, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Row scope key column '{keyColumn}' collides with reserved column '{RowScopeFlagColumn}'.");
            }
        }

        string keyList = string.Join(", ", keyColumns.Select(c => dialect.QuoteIdentifier(c)));
        string from = dialect.FormatTableIdentifier(table.Identifier) + " AS " + dialect.QuoteIdentifier(TrinoSqlEngine.RowFilterAliases.Target);
        string qFlag = dialect.QuoteIdentifier(RowScopeFlagColumn);
        string qDerived = dialect.QuoteIdentifier(RowScopeDerivedAlias);

        var predicateParts = new List<string>();
        if (tenantColumn != null)
        {
            predicateParts.Add($"{dialect.QuoteIdentifier(tenantColumn)} = @rs_tenant");
        }

        if (!string.IsNullOrWhiteSpace(filterSql))
        {
            predicateParts.Add($"({filterSql})");
        }

        string rowCondition = predicateParts.Count > 0 ? string.Join(" AND ", predicateParts) : "1 = 1";

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

        return $"SELECT {keyList} FROM (SELECT {keyList}, CASE WHEN {rowCondition} THEN 1 ELSE 0 END AS {qFlag} FROM {from} WHERE ({tupleSql})) AS {qDerived} GROUP BY {keyList} HAVING COUNT(*) = SUM({qFlag})";
    }

    private static void AddParameter(DbCommand cmd, string name, object? value, ISet<string>? bound = null)
    {
        if (bound != null && !bound.Add(Autheris.Application.Sql.Policy.RowFilterParameterNames.Bare(name)))
        {
            throw new System.Security.SecurityException("A statement parameter name is bound twice (fail-closed).");
        }

        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
