namespace Autheris.Application.Usage;

using System.Threading;

public sealed class UsageSeriesCounters
{
    private long _requests;
    private long _queries;
    private long _rows;
    private long _rowsMeasured;
    private long _bytes;
    private long _durationMs;
    private long _analysisCost;
    private long _sourceComputeMs;
    private long _sourceComputeMeasured;
    private long _sourceCostMicros;
    private long _sourceCostMeasured;
    private long _allow;
    private long _denyAccess;
    private long _denyRateLimit;
    private long _denyCost;
    private long _denyQuota;
    private long _denyOther;
    private long _errors;

    public long Requests => Volatile.Read(ref _requests);
    public long Queries => Volatile.Read(ref _queries);
    public long Rows => Volatile.Read(ref _rows);
    public long RowsMeasured => Volatile.Read(ref _rowsMeasured);
    public long Bytes => Volatile.Read(ref _bytes);
    public long DurationMs => Volatile.Read(ref _durationMs);
    public long AnalysisCost => Volatile.Read(ref _analysisCost);
    public long SourceComputeMs => Volatile.Read(ref _sourceComputeMs);
    public long SourceComputeMeasured => Volatile.Read(ref _sourceComputeMeasured);
    public long SourceCostMicros => Volatile.Read(ref _sourceCostMicros);
    public long SourceCostMeasured => Volatile.Read(ref _sourceCostMeasured);
    public long Allow => Volatile.Read(ref _allow);
    public long DenyAccess => Volatile.Read(ref _denyAccess);
    public long DenyRateLimit => Volatile.Read(ref _denyRateLimit);
    public long DenyCost => Volatile.Read(ref _denyCost);
    public long DenyQuota => Volatile.Read(ref _denyQuota);
    public long DenyOther => Volatile.Read(ref _denyOther);
    public long Errors => Volatile.Read(ref _errors);

    public void Add(
        long requests,
        long queries,
        long rows,
        bool rowsMeasured,
        long bytes,
        long durationMs,
        long analysisCost,
        long? sourceComputeMs,
        long? sourceCostMicros,
        UsageOutcome outcome)
    {
        if (requests != 0) Interlocked.Add(ref _requests, requests);
        if (queries != 0) Interlocked.Add(ref _queries, queries);
        if (rows != 0) Interlocked.Add(ref _rows, rows);
        if (rowsMeasured) Interlocked.Increment(ref _rowsMeasured);
        if (bytes != 0) Interlocked.Add(ref _bytes, bytes);
        if (durationMs != 0) Interlocked.Add(ref _durationMs, durationMs);
        if (analysisCost != 0) Interlocked.Add(ref _analysisCost, analysisCost);
        AddSourceStats(sourceComputeMs, sourceCostMicros);

        switch (outcome)
        {
            case UsageOutcome.Allow:
                Interlocked.Increment(ref _allow);
                break;
            case UsageOutcome.DenyAccess:
                Interlocked.Increment(ref _denyAccess);
                break;
            case UsageOutcome.DenyRateLimit:
                Interlocked.Increment(ref _denyRateLimit);
                break;
            case UsageOutcome.DenyCost:
                Interlocked.Increment(ref _denyCost);
                break;
            case UsageOutcome.DenyQuota:
                Interlocked.Increment(ref _denyQuota);
                break;
            case UsageOutcome.DenyOther:
                Interlocked.Increment(ref _denyOther);
                break;
            case UsageOutcome.Error:
                Interlocked.Increment(ref _errors);
                break;
        }
    }

    public void AddSourceStats(long? sourceComputeMs, long? sourceCostMicros)
    {
        if (sourceComputeMs.HasValue)
        {
            Interlocked.Add(ref _sourceComputeMs, sourceComputeMs.Value);
            Interlocked.Increment(ref _sourceComputeMeasured);
        }

        if (sourceCostMicros.HasValue)
        {
            Interlocked.Add(ref _sourceCostMicros, sourceCostMicros.Value);
            Interlocked.Increment(ref _sourceCostMeasured);
        }
    }

    public void MergeFrom(UsageSeriesCounters source)
    {
        ArgumentNullException.ThrowIfNull(source);
        AddIfNonZero(ref _requests, source.Requests);
        AddIfNonZero(ref _queries, source.Queries);
        AddIfNonZero(ref _rows, source.Rows);
        AddIfNonZero(ref _rowsMeasured, source.RowsMeasured);
        AddIfNonZero(ref _bytes, source.Bytes);
        AddIfNonZero(ref _durationMs, source.DurationMs);
        AddIfNonZero(ref _analysisCost, source.AnalysisCost);
        AddIfNonZero(ref _sourceComputeMs, source.SourceComputeMs);
        AddIfNonZero(ref _sourceComputeMeasured, source.SourceComputeMeasured);
        AddIfNonZero(ref _sourceCostMicros, source.SourceCostMicros);
        AddIfNonZero(ref _sourceCostMeasured, source.SourceCostMeasured);
        AddIfNonZero(ref _allow, source.Allow);
        AddIfNonZero(ref _denyAccess, source.DenyAccess);
        AddIfNonZero(ref _denyRateLimit, source.DenyRateLimit);
        AddIfNonZero(ref _denyCost, source.DenyCost);
        AddIfNonZero(ref _denyQuota, source.DenyQuota);
        AddIfNonZero(ref _denyOther, source.DenyOther);
        AddIfNonZero(ref _errors, source.Errors);
    }

    private static void AddIfNonZero(ref long target, long delta)
    {
        if (delta != 0)
        {
            Interlocked.Add(ref target, delta);
        }
    }

    public static UsageSeriesCounters FromTotals(
        long requests, long queries, long rows, long rowsMeasured, long bytes, long durationMs, long analysisCost,
        long sourceComputeMs, long sourceComputeMeasured, long sourceCostMicros, long sourceCostMeasured,
        long allow, long denyAccess, long denyRateLimit, long denyCost, long denyQuota, long denyOther, long errors) =>
        new()
        {
            _requests = requests,
            _queries = queries,
            _rows = rows,
            _rowsMeasured = rowsMeasured,
            _bytes = bytes,
            _durationMs = durationMs,
            _analysisCost = analysisCost,
            _sourceComputeMs = sourceComputeMs,
            _sourceComputeMeasured = sourceComputeMeasured,
            _sourceCostMicros = sourceCostMicros,
            _sourceCostMeasured = sourceCostMeasured,
            _allow = allow,
            _denyAccess = denyAccess,
            _denyRateLimit = denyRateLimit,
            _denyCost = denyCost,
            _denyQuota = denyQuota,
            _denyOther = denyOther,
            _errors = errors
        };
}
