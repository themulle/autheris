namespace Autheris.Application.Data.Relations;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
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
/// <para>VF-02: the statement is built from catalog-checked identifiers only (quoted per dialect, no <c>SELECT *</c>), every value is bound
/// with the dialect's marker style, a tenanted target always gets a bound tenant predicate, and the database session (tenant and user)
/// is initialized before the read, so database-side row security applies.</para>
/// </summary>
public sealed class GovernedCrossSourceRowReader : ICrossSourceRowReader
{
    private const string TenantParameter = "vf_tenant";

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IDbSessionContextInitializer _sessionInitializer;

    public GovernedCrossSourceRowReader(
        ISqlConnectionFactory connectionFactory,
        IOptions<GatewayOptions> gatewayOptions,
        IDbSessionContextInitializer sessionInitializer)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _sessionInitializer = sessionInitializer ?? throw new ArgumentNullException(nameof(sessionInitializer));
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByKeysAsync(
        CrossSourceKeyReadRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.JoinKeyColumns);
        ArgumentNullException.ThrowIfNull(request.ProjectedColumns);
        ArgumentNullException.ThrowIfNull(request.Keys);

        var targetTable = request.Target;
        if (request.Keys.Count == 0 || request.JoinKeyColumns.Count == 0)
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

        var dialect = targetTable.Dialect;
        var (sql, parameters) = BuildStatement(request);

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);

        // PostgreSQL: read-only transaction (a relation read must not change data), never committed.
        await using DbTransaction? tx = dialect == DatabaseDialect.PostgreSql
            ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        if (tx != null)
        {
            await using var readOnly = connection.CreateCommand();
            readOnly.Transaction = tx;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Fail closed: a failing session initialization fails the request; there is no unscoped read.
        await _sessionInitializer.InitializeSessionAsync(connection, tx, dialect, request.Tenant, request.UserSid, purpose: null, ct: ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = Math.Max(1, connOptions.CommandTimeoutSeconds);
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
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
        }

        if (tx != null)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return rows;
    }

    /// <summary>The statement and its parameters for <paramref name="request"/>; every identifier is the quoted catalog spelling, every value is a parameter.</summary>
    internal static (string Sql, IReadOnlyList<(string Name, object Value)> Parameters) BuildStatement(CrossSourceKeyReadRequest request)
    {
        var targetTable = request.Target;
        var dialect = targetTable.Dialect;

        // Every identifier comes from the catalog spelling of the target; an unknown column is an error, never a guess.
        var joinKeys = request.JoinKeyColumns.Select(c => CatalogColumn(targetTable, c)).ToList();
        var tenantColumn = targetTable.TenantColumnName;
        var projected = new List<string>();
        foreach (var name in request.ProjectedColumns.Select(c => CatalogColumn(targetTable, c)).Concat(joinKeys).Concat(tenantColumn is null ? Array.Empty<string>() : new[] { tenantColumn }))
        {
            if (!projected.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                projected.Add(name);
            }
        }

        var parameters = new List<(string Name, object Value)>();
        var sb = new StringBuilder("SELECT ");
        sb.Append(string.Join(", ", projected.Select(c => dialect.QuoteIdentifier(c))));
        sb.Append(" FROM ").Append(dialect.FormatTableIdentifier(targetTable.Identifier)).Append(" WHERE (");

        if (joinKeys.Count == 1)
        {
            sb.Append(dialect.QuoteIdentifier(joinKeys[0])).Append(" IN (");
            for (int i = 0; i < request.Keys.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var name = string.Create(CultureInfo.InvariantCulture, $"p{i}");
                sb.Append(dialect.FormatParameterMarker(name));
                parameters.Add((dialect.FormatParameterName(name), request.Keys[i].Values[0] ?? DBNull.Value));
            }

            sb.Append(')');
        }
        else
        {
            for (int i = 0; i < request.Keys.Count; i++)
            {
                if (i > 0) sb.Append(" OR ");
                sb.Append('(');
                for (int j = 0; j < joinKeys.Count; j++)
                {
                    if (j > 0) sb.Append(" AND ");
                    var name = string.Create(CultureInfo.InvariantCulture, $"p{i}_{j}");
                    sb.Append(dialect.QuoteIdentifier(joinKeys[j])).Append(" = ").Append(dialect.FormatParameterMarker(name));
                    parameters.Add((dialect.FormatParameterName(name), request.Keys[i].Values[j] ?? DBNull.Value));
                }

                sb.Append(')');
            }
        }

        sb.Append(')');

        if (tenantColumn != null)
        {
            // A tenanted target is always restricted to the caller's tenant in the database, whatever the row policy says.
            sb.Append(" AND ").Append(dialect.QuoteIdentifier(tenantColumn)).Append(" = ").Append(dialect.FormatParameterMarker(TenantParameter));
            parameters.Add((dialect.FormatParameterName(TenantParameter), request.Tenant.Value));
        }

        return (sb.ToString(), parameters);
    }

    private static string CatalogColumn(TableMetadata target, string requested) =>
        target.GetColumn(requested)?.ColumnName
        ?? throw new InvalidOperationException($"The column '{requested}' does not exist on '{target.Identifier}'.");
}
