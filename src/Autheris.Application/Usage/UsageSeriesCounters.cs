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
}
