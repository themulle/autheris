namespace Autheris.Application.Telemetry;

using System;
using System.Threading;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Provides cached, categorized loggers for individual data sources (Autheris.DataSource.&lt;normalized name&gt;).
/// Static facade delegating to <see cref="IDataSourceLoggers"/>.
/// </summary>
public static partial class DataSourceLoggers
{
    private static readonly IDataSourceLoggers BuiltIn = new DataSourceLoggersService(NullLoggerFactory.Instance);
    private static IDataSourceLoggers _current = BuiltIn;

    /// <summary>
    /// The active <see cref="IDataSourceLoggers"/> provider.
    /// </summary>
    public static IDataSourceLoggers Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? BuiltIn);
    }

    /// <summary>
    /// Initializes the logger factory used to produce data source category loggers.
    /// </summary>
    public static void Initialize(ILoggerFactory factory)
    {
        Current = new DataSourceLoggersService(factory);
    }

    /// <summary>
    /// Gets or creates a cached logger for the specified data source name.
    /// Category: Autheris.DataSource.&lt;normalized name&gt;
    /// </summary>
    public static ILogger Get(string? dataSourceName) => Current.Get(dataSourceName);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Slow query detected on data source '{DataSourceName}', operation '{OperationName}', collection '{CollectionName}', elapsed {ElapsedMilliseconds:F2} ms (threshold {ThresholdMilliseconds} ms).")]
    public static partial void LogSlowQuery(
        ILogger logger,
        string dataSourceName,
        string operationName,
        string? collectionName,
        double elapsedMilliseconds,
        double thresholdMilliseconds);
}
