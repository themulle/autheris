namespace Autheris.Application.Procedures.Services;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02 / ADR-018: Opens connections with the dedicated EXECUTE-only technical login. Phase 1 supports SQL Server only.
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

        if (!Autheris.Application.Sql.Services.GovernedSqlExecutionService.TryMapProviderToDialect(conn.Provider, out var dialect) ||
            dialect != Autheris.Domain.Common.DatabaseDialect.SqlServer)
        {
            throw new InvalidOperationException("Stored procedure endpoints currently support SQL Server only.");
        }

        return conn;
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
