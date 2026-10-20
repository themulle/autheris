namespace Autheris.Api.Hosting.Warmup;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// SEC-PERF254-02 / WP-5: Pre-warms configured SQL data sources by establishing connections
/// and executing read-only probe (SELECT 1). Strictly forbidden from executing DDL/DML or writing audit/consent data.
/// </summary>
public sealed class DataSourceConnectionWarmup : IStartupWarmup
{
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ILogger<DataSourceConnectionWarmup>? _logger;

    public DataSourceConnectionWarmup(
        ISqlConnectionFactory? connectionFactory = null,
        IOptions<GatewayOptions>? options = null,
        ILogger<DataSourceConnectionWarmup>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
    }

    public string Name => StartupWarmupOptions.DataSourceConnectionStep;

    public WarmupKind Kind => WarmupKind.BestEffort;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_connectionFactory == null || _options?.Value?.DataSources == null)
        {
            return;
        }

        foreach (var (dsName, connOptions) in _options.Value.DataSources.Connections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString))
            {
                continue;
            }

            try
            {
                await using var conn = await _connectionFactory.CreateOpenConnectionAsync(connOptions, cancellationToken).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                _logger?.LogDebug("Data source {DataSource} pre-warmed successfully.", dsName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Data source {DataSource} warm-up ping failed; continuing best-effort.", dsName);
            }
        }
    }
}
