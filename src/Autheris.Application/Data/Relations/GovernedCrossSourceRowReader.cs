namespace Autheris.Application.Data.Relations;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #223 / WP-FED-06: Production implementation of <see cref="ICrossSourceRowReader"/> that executes
/// real parameterized queries against the target data source via <see cref="ISqlConnectionFactory"/>.
/// </summary>
public sealed class GovernedCrossSourceRowReader : ICrossSourceRowReader
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IOptions<GatewayOptions> _gatewayOptions;

    public GovernedCrossSourceRowReader(
        ISqlConnectionFactory connectionFactory,
        IOptions<GatewayOptions> gatewayOptions)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByKeysAsync(
        TableMetadata targetTable,
        IReadOnlyList<string> joinKeyColumns,
        IReadOnlyList<CompositeKey> batchKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetTable);
        ArgumentNullException.ThrowIfNull(joinKeyColumns);
        ArgumentNullException.ThrowIfNull(batchKeys);

        if (batchKeys.Count == 0 || joinKeyColumns.Count == 0)
        {
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        var sourceName = string.IsNullOrWhiteSpace(targetTable.Table.SourceName)
            ? targetTable.Identifier.Domain
            : targetTable.Table.SourceName;

        var connections = _gatewayOptions.Value.DataSources?.Connections;
        if (connections == null || !connections.TryGetValue(sourceName, out var connOptions) || string.IsNullOrWhiteSpace(connOptions.ConnectionString))
        {
            throw new InvalidOperationException($"The data source '{sourceName}' has no database connection configured.");
        }

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, connOptions.CommandTimeoutSeconds);

        var sb = new StringBuilder();
        sb.Append("SELECT * FROM ");
        if (!string.IsNullOrWhiteSpace(targetTable.Identifier.Schema))
        {
            sb.Append(targetTable.Identifier.Schema).Append('.');
        }
        sb.Append(targetTable.Identifier.TableName);
        sb.Append(" WHERE ");

        if (joinKeyColumns.Count == 1)
        {
            var keyCol = joinKeyColumns[0];
            sb.Append(keyCol).Append(" IN (");
            for (int i = 0; i < batchKeys.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var paramName = $"@p{i}";
                sb.Append(paramName);
                var p = command.CreateParameter();
                p.ParameterName = paramName;
                p.Value = batchKeys[i].Values[0] ?? DBNull.Value;
                command.Parameters.Add(p);
            }
            sb.Append(')');
        }
        else
        {
            for (int i = 0; i < batchKeys.Count; i++)
            {
                if (i > 0) sb.Append(" OR ");
                sb.Append('(');
                for (int j = 0; j < joinKeyColumns.Count; j++)
                {
                    if (j > 0) sb.Append(" AND ");
                    var paramName = $"@p{i}_{j}";
                    sb.Append(joinKeyColumns[j]).Append(" = ").Append(paramName);
                    var p = command.CreateParameter();
                    p.ParameterName = paramName;
                    p.Value = batchKeys[i].Values[j] ?? DBNull.Value;
                    command.Parameters.Add(p);
                }
                sb.Append(')');
            }
        }

        command.CommandText = sb.ToString();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var colNames = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            colNames[i] = reader.GetName(i);
        }

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var dict = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                dict[colNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(dict);
        }

        return rows;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsForFilterAsync(
        TableMetadata targetTable,
        string filterSql,
        int maxRows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetTable);

        var sourceName = string.IsNullOrWhiteSpace(targetTable.Table.SourceName)
            ? targetTable.Identifier.Domain
            : targetTable.Table.SourceName;

        var connections = _gatewayOptions.Value.DataSources?.Connections;
        if (connections == null || !connections.TryGetValue(sourceName, out var connOptions) || string.IsNullOrWhiteSpace(connOptions.ConnectionString))
        {
            throw new InvalidOperationException($"The data source '{sourceName}' has no database connection configured.");
        }

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, connOptions.CommandTimeoutSeconds);

        var sb = new StringBuilder();
        sb.Append("SELECT * FROM ");
        if (!string.IsNullOrWhiteSpace(targetTable.Identifier.Schema))
        {
            sb.Append(targetTable.Identifier.Schema).Append('.');
        }
        sb.Append(targetTable.Identifier.TableName);
        if (!string.IsNullOrWhiteSpace(filterSql))
        {
            sb.Append(" WHERE ").Append(filterSql);
        }

        command.CommandText = sb.ToString();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var colNames = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            colNames[i] = reader.GetName(i);
        }

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var dict = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                dict[colNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(dict);
            if (maxRows > 0 && rows.Count >= maxRows)
            {
                break;
            }
        }

        return rows;
    }
}
