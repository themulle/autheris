namespace Autheris.Application.Telemetry;

using System;
using System.Collections.Concurrent;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Provides cached, categorized loggers for individual data sources (Autheris.DataSource.&lt;normalized name&gt;).
/// </summary>
public static partial class DataSourceLoggers
{
    private static readonly ConcurrentDictionary<string, ILogger> Loggers = new(StringComparer.Ordinal);
    private static ILoggerFactory s_factory = NullLoggerFactory.Instance;

    /// <summary>
    /// Initializes the logger factory used to produce data source category loggers.
    /// </summary>
    public static void Initialize(ILoggerFactory factory)
    {
        s_factory = factory ?? NullLoggerFactory.Instance;
        Loggers.Clear();
    }

    /// <summary>
    /// Gets or creates a cached logger for the specified data source name.
    /// Category: Autheris.DataSource.&lt;normalized name&gt;
    /// </summary>
    public static ILogger Get(string? dataSourceName)
    {
        var normalized = TelemetryNames.DataSource(dataSourceName);
        return Loggers.GetOrAdd(normalized, key => s_factory.CreateLogger($"Autheris.DataSource.{key}"));
    }

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
