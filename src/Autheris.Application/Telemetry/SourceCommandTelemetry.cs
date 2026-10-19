namespace Autheris.Application.Telemetry;

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;

/// <summary>
/// Descriptor parameter for beginning a database source command telemetry scope.
/// </summary>
public readonly record struct SourceCommandDescriptor(
    string DataSourceName,
    string DbSystemName,
    string? Namespace,
    string OperationName,
    string? CollectionName,
    string? CompiledSql,
    DatabaseDialect? Dialect);

/// <summary>
/// Data-path telemetry hook producing client spans and metrics for database operations.
/// </summary>
public static class SourceCommandTelemetry
{
    private static readonly ActivitySource Source = new("Autheris.Core");
    private static readonly Meter Meter = new("Autheris.Core");

    private static readonly Histogram<double> DbClientOperationDuration =
        Meter.CreateHistogram<double>("db.client.operation.duration", "s", "Duration of database client operations.");

    private static readonly Counter<long> SlowQueriesCounter =
        Meter.CreateCounter<long>("autheris.query.slow", "{query}", "Number of slow queries exceeding threshold.");

    private static readonly Counter<long> QueryRowsCounter =
        Meter.CreateCounter<long>("autheris.query.rows", "{row}", "Number of rows returned or affected.");

    private static readonly Counter<long> QueryBytesCounter =
        Meter.CreateCounter<long>("autheris.query.bytes", "By", "Number of bytes returned or transferred.");

    private static IDataSourceTelemetryPolicyProvider? s_defaultPolicyProvider;

    public static void Initialize(IDataSourceTelemetryPolicyProvider policyProvider)
    {
        s_defaultPolicyProvider = policyProvider;
    }

    public static SourceCommandScope Start(
        in SourceCommandDescriptor descriptor,
        IDataSourceTelemetryPolicyProvider? policyProvider = null)
    {
        var provider = policyProvider ?? s_defaultPolicyProvider;
        var policy = provider?.GetPolicy(descriptor.DataSourceName) ?? new DataSourceTelemetryPolicy(
            Microsoft.Extensions.Logging.LogLevel.Information,
            SpanDetail.Basic,
            StatementTextMode.None,
            TimeSpan.FromMilliseconds(500),
            true);

        var normalizedDataSource = TelemetryNames.DataSource(descriptor.DataSourceName);
        var spanName = string.IsNullOrWhiteSpace(descriptor.CollectionName)
            ? descriptor.OperationName
            : $"{descriptor.OperationName} {descriptor.CollectionName}";

        Activity? activity = null;
        if (policy.SpanDetail != SpanDetail.None && Source.HasListeners())
        {
            activity = Source.StartActivity(spanName, ActivityKind.Client);
            if (activity != null && activity.IsAllDataRequested)
            {
                activity.SetTag(TelemetryAttributes.DbSystemName, descriptor.DbSystemName);
                if (!string.IsNullOrWhiteSpace(descriptor.Namespace))
                {
                    activity.SetTag(TelemetryAttributes.DbNamespace, descriptor.Namespace);
                }
                activity.SetTag(TelemetryAttributes.DbOperationName, descriptor.OperationName);
                if (!string.IsNullOrWhiteSpace(descriptor.CollectionName))
                {
                    activity.SetTag(TelemetryAttributes.DbCollectionName, descriptor.CollectionName);
                }
                activity.SetTag(TelemetryAttributes.DbQuerySummary, spanName);
                activity.SetTag(TelemetryAttributes.AutherisDataSourceName, normalizedDataSource);

                if (!string.IsNullOrWhiteSpace(descriptor.CompiledSql))
                {
                    if (SqlTextScrubber.TryScrub(descriptor.CompiledSql, descriptor.Dialect ?? DatabaseDialect.PostgreSql, out var scrubbedSql, out var hashHex))
                    {
                        activity.SetTag(TelemetryAttributes.AutherisDbQueryHash, hashHex);
                        if (policy.StatementText == StatementTextMode.Parameterized)
                        {
                            activity.SetTag(TelemetryAttributes.DbQueryText, scrubbedSql);
                        }
                    }
                }
            }
        }

        return new SourceCommandScope(
            activity,
            descriptor,
            normalizedDataSource,
            policy,
            Stopwatch.GetTimestamp());
    }

    internal static void RecordDuration(double durationSeconds, ref TagList tags) =>
        DbClientOperationDuration.Record(durationSeconds, tags);

    internal static void RecordSlowQuery(ref TagList tags) =>
        SlowQueriesCounter.Add(1, tags);

    internal static void RecordRows(long rows, ref TagList tags) =>
        QueryRowsCounter.Add(rows, tags);

    internal static void RecordBytes(long bytes, ref TagList tags) =>
        QueryBytesCounter.Add(bytes, tags);
}

/// <summary>
/// Scoped handle for tracking a running database source command.
/// Struct type to ensure zero heap allocations when telemetry is disabled or unsampled.
/// </summary>
public struct SourceCommandScope : IDisposable
{
    private readonly Activity? _activity;
    private readonly SourceCommandDescriptor _descriptor;
    private readonly string _normalizedDataSource;
    private readonly DataSourceTelemetryPolicy _policy;
    private readonly long _startTimestamp;
    private string? _errorType;
    private int? _statusCode;
    private long _rows;
    private long _bytes;
    private bool _disposed;

    internal SourceCommandScope(
        Activity? activity,
        in SourceCommandDescriptor descriptor,
        string normalizedDataSource,
        DataSourceTelemetryPolicy policy,
        long startTimestamp)
    {
        _activity = activity;
        _descriptor = descriptor;
        _normalizedDataSource = normalizedDataSource;
        _policy = policy;
        _startTimestamp = startTimestamp;
    }

    public void SetRows(long rows)
    {
        _rows = rows;
        if (_policy.RecordRowCounts)
        {
            if (_activity != null && _activity.IsAllDataRequested)
            {
                _activity.SetTag(TelemetryAttributes.AutherisDbRows, rows);
            }
        }
    }

    public void SetBytes(long bytes)
    {
        _bytes = bytes;
        if (_policy.RecordRowCounts)
        {
            if (_activity != null && _activity.IsAllDataRequested)
            {
                _activity.SetTag(TelemetryAttributes.AutherisDbBytes, bytes);
            }
        }
    }

    public void RecordFirstRow()
    {
        if (_policy.SpanDetail == SpanDetail.Detailed && _activity != null)
        {
            _activity.AddEvent(new ActivityEvent("autheris.db.first_row"));
        }
    }

    public void RecordStreamEnd()
    {
        if (_policy.SpanDetail == SpanDetail.Detailed && _activity != null)
        {
            _activity.AddEvent(new ActivityEvent("autheris.db.stream_end"));
        }
    }

    public void Fail(Exception exception)
    {
        if (exception == null) return;
        _errorType = TelemetryExceptionPolicy.ErrorType(exception);
        _statusCode = TelemetryExceptionPolicy.GetDriverErrorCode(exception);

        if (_activity != null && _activity.IsAllDataRequested)
        {
            _activity.SetStatus(ActivityStatusCode.Error, _errorType);
            _activity.SetTag(TelemetryAttributes.ErrorType, _errorType);
            if (_statusCode.HasValue)
            {
                _activity.SetTag(TelemetryAttributes.DbResponseStatusCode, _statusCode.Value);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var elapsedSeconds = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        var elapsedMs = elapsedSeconds * 1000.0;

        if (_policy.SlowQueryThreshold.HasValue && elapsedMs > _policy.SlowQueryThreshold.Value.TotalMilliseconds)
        {
            if (_activity != null && _activity.IsAllDataRequested)
            {
                _activity.AddEvent(new ActivityEvent("autheris.db.slow_query"));
            }

            var slowTags = new TagList
            {
                { TelemetryAttributes.DbSystemName, _descriptor.DbSystemName },
                { TelemetryAttributes.AutherisDataSourceName, _normalizedDataSource },
                { TelemetryAttributes.DbOperationName, _descriptor.OperationName }
            };
            SourceCommandTelemetry.RecordSlowQuery(ref slowTags);

            var logger = DataSourceLoggers.Get(_normalizedDataSource);
            DataSourceLoggers.LogSlowQuery(
                logger,
                _normalizedDataSource,
                _descriptor.OperationName,
                _descriptor.CollectionName,
                elapsedMs,
                _policy.SlowQueryThreshold.Value.TotalMilliseconds);
        }

        var tags = new TagList
        {
            { TelemetryAttributes.DbSystemName, _descriptor.DbSystemName },
            { TelemetryAttributes.AutherisDataSourceName, _normalizedDataSource },
            { TelemetryAttributes.DbOperationName, _descriptor.OperationName }
        };

        if (!string.IsNullOrEmpty(_errorType))
        {
            tags.Add(TelemetryAttributes.ErrorType, _errorType);
        }

        SourceCommandTelemetry.RecordDuration(elapsedSeconds, ref tags);

        if (_policy.RecordRowCounts)
        {
            if (_rows > 0)
            {
                var rowTags = new TagList
                {
                    { TelemetryAttributes.AutherisDataSourceName, _normalizedDataSource }
                };
                SourceCommandTelemetry.RecordRows(_rows, ref rowTags);
            }

            if (_bytes > 0)
            {
                var byteTags = new TagList
                {
                    { TelemetryAttributes.AutherisDataSourceName, _normalizedDataSource }
                };
                SourceCommandTelemetry.RecordBytes(_bytes, ref byteTags);
            }
        }

        _activity?.Dispose();
    }
}
