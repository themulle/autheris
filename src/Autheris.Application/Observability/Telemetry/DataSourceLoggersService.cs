namespace Autheris.Application.Observability.Telemetry;

using System;
using System.Collections.Concurrent;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Default implementation of <see cref="IDataSourceLoggers"/> backed by an injected <see cref="ILoggerFactory"/>.
/// </summary>
public sealed class DataSourceLoggersService : IDataSourceLoggers
{
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly ILoggerFactory _factory;

    public DataSourceLoggersService(ILoggerFactory? factory = null)
    {
        _factory = factory ?? NullLoggerFactory.Instance;
    }

    public ILogger Get(string? dataSourceName)
    {
        var normalized = TelemetryNames.DataSource(dataSourceName);
        return _loggers.GetOrAdd(normalized, key => _factory.CreateLogger($"Autheris.DataSource.{key}"));
    }
}
