namespace Autheris.Application.Procedures.Services;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02 / ADR-018: Opens connections with the dedicated EXECUTE-only technical login. SQL Server and PostgreSQL only.
/// </summary>
public sealed class ProcedureConnectionProvider
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IOptions<GatewayOptions> _options;

    public ProcedureConnectionProvider(ISqlConnectionFactory connectionFactory, IOptions<GatewayOptions> options)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Resolves the connection options for a declaration (data source name or the configured procedure connection).</summary>
    public DataSourceConnectionOptions Resolve(ProcedureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        string name = string.IsNullOrWhiteSpace(definition.DataSource)
            ? _options.Value.SqlEndpoints.Procedures.ConnectionName
            : definition.DataSource;

        var connections = _options.Value.DataSources?.Connections;
        if (connections == null || !connections.TryGetValue(name, out var conn) || string.IsNullOrWhiteSpace(conn.ConnectionString))
        {
            throw new InvalidOperationException($"No connection string is configured for the procedure data source '{name}'.");
        }

        // closed dialect set (SQL Server, PostgreSQL); PostgreSQL only with table-valued functions.
        string? rejection = CheckAdmission(_options.Value, name, definition.Kind);
        if (rejection != null)
        {
            throw new InvalidOperationException(rejection);
        }

        return conn;
    }

    /// <summary>the closed dialect set of procedure endpoints.</summary>
    public static bool IsAdmittedDialect(Autheris.Domain.Common.DatabaseDialect dialect) =>
        dialect is Autheris.Domain.Common.DatabaseDialect.SqlServer or Autheris.Domain.Common.DatabaseDialect.PostgreSql;

    /// <summary>Fixed rejection text; names the data source, never the connection string.</summary>
    public static string UnsupportedDialectMessage(string dataSourceName) =>
        $"Stored procedure endpoints support SQL Server and PostgreSQL only (data source '{dataSourceName}').";

    /// <summary>
    /// null when the data source exists with an admitted dialect (and, for PostgreSQL, the declared kind is a
    /// table-valued function); otherwise the fixed rejection text. Shared by startup, loader and connection provider.
    /// </summary>
    public static string? CheckAdmission(GatewayOptions options, string dataSourceName, ProcedureKind? kind = null)
    {
        var connections = options.DataSources?.Connections;
        if (connections == null || !connections.TryGetValue(dataSourceName, out var conn) || string.IsNullOrWhiteSpace(conn.ConnectionString) ||
            !Autheris.Domain.Common.DataSourceProvider.TryResolveDialect(conn.Provider, out var dialect) || !IsAdmittedDialect(dialect))
        {
            return UnsupportedDialectMessage(dataSourceName);
        }

        return dialect == Autheris.Domain.Common.DatabaseDialect.PostgreSql && kind is { } k && k != ProcedureKind.TableValuedFunction
            ? $"PostgreSQL procedure endpoints support table-valued functions only (data source '{dataSourceName}')."
            : null;
    }

    public string ResolveName(ProcedureDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.DataSource)
            ? _options.Value.SqlEndpoints.Procedures.ConnectionName
            : definition.DataSource;

    public async Task<(DbConnection Connection, DataSourceConnectionOptions Options)> OpenAsync(
        ProcedureDefinition definition,
        CancellationToken ct)
    {
        var options = Resolve(definition);
        var connection = await _connectionFactory.CreateOpenConnectionAsync(options, ct).ConfigureAwait(false);
        return (connection, options);
    }
}
