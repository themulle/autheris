namespace Autheris.Extensions.Cdc.CustomQuery;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Abstraction for creating database connections to heterogeneous source databases (PLAN-CDC-101 D-A).
/// </summary>
public interface IDataSourceConnectionFactory
{
    Task<DbConnection> CreateConnectionAsync(string dataSourceName, CancellationToken ct = default);
}

public sealed class DefaultDataSourceConnectionFactory : IDataSourceConnectionFactory
{
    private readonly ISqlConnectionFactory _sqlConnectionFactory;
    private readonly IOptionsMonitor<GatewayOptions> _gatewayOptions;

    public DefaultDataSourceConnectionFactory(
        ISqlConnectionFactory sqlConnectionFactory,
        IOptionsMonitor<GatewayOptions> gatewayOptions)
    {
        _sqlConnectionFactory = sqlConnectionFactory ?? throw new ArgumentNullException(nameof(sqlConnectionFactory));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
    }

    public async Task<DbConnection> CreateConnectionAsync(string dataSourceName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSourceName);

        if (_gatewayOptions.CurrentValue.DataSources.Connections.TryGetValue(dataSourceName, out var dsOptions))
        {
            return await _sqlConnectionFactory.CreateOpenConnectionAsync(dsOptions, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Data source '{dataSourceName}' is not configured in Gateway:DataSources:Connections.");
    }
}
