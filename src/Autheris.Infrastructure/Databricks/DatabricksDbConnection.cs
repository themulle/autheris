namespace Autheris.Infrastructure.Databricks;

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Autheris.Domain.Options;

/// <summary>
/// ADO.NET facade over <see cref="DatabricksStatementExecutionClient"/> (WP-X0j). There is no session and no transaction: Delta
/// statements are independent, so <see cref="BeginTransaction"/> is refused (fail closed) and nothing is pooled. Opening does not
/// contact the workspace; every statement goes through the hardened REST client (SEC-ADG-09).
/// </summary>
public sealed class DatabricksDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    public DatabricksDbConnection(
        DatabricksStatementExecutionClient client,
        DatabricksDataSourceOptions options,
        string? dataSourceName,
        Autheris.Domain.Connectors.DelegationContext? delegationContext = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        DataSourceName = dataSourceName;
        DelegationContext = delegationContext;
    }

    internal DatabricksStatementExecutionClient Client { get; }
    internal DatabricksDataSourceOptions Options { get; }
    public Autheris.Domain.Connectors.DelegationContext? DelegationContext { get; set; }

    /// <summary>The configured data source name; null lets the client pick the single configured Databricks source.</summary>
    internal string? DataSourceName { get; }

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => Options.Catalog;
    public override string DataSource => Options.Host;
    public override string ServerVersion => string.Empty;
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException("The Databricks catalog is fixed by the data source configuration.");

    public override void Open() => _state = ConnectionState.Open;
    public override Task OpenAsync(CancellationToken cancellationToken) { _state = ConnectionState.Open; return Task.CompletedTask; }
    public override void Close() => _state = ConnectionState.Closed;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("Databricks has no transaction.");

    protected override DbCommand CreateDbCommand() => new DatabricksDbCommand { Connection = this };
}
