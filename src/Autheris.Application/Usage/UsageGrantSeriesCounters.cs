namespace Autheris.Application.Usage;

using System.Threading;

public sealed class UsageGrantSeriesCounters
{
    private long _accesses;
    private long _queries;
    private long _rows;
    private long _bytes;
    private long _analysisCost;
    private long _sourceComputeMs;
    private long _sourceCostMicros;

    public long Accesses => Volatile.Read(ref _accesses);
    public long Queries => Volatile.Read(ref _queries);
    public long Rows => Volatile.Read(ref _rows);
    public long Bytes => Volatile.Read(ref _bytes);
    public long AnalysisCost => Volatile.Read(ref _analysisCost);
    public long SourceComputeMs => Volatile.Read(ref _sourceComputeMs);
    public long SourceCostMicros => Volatile.Read(ref _sourceCostMicros);

    public void Add(
        long accesses,
        long queries,
        long rows,
        long bytes,
        long analysisCost,
        long? sourceComputeMs,
        long? sourceCostMicros)
    {
        if (accesses != 0) Interlocked.Add(ref _accesses, accesses);
        if (queries != 0) Interlocked.Add(ref _queries, queries);
        if (rows != 0) Interlocked.Add(ref _rows, rows);
        if (bytes != 0) Interlocked.Add(ref _bytes, bytes);
        if (analysisCost != 0) Interlocked.Add(ref _analysisCost, analysisCost);
        if (sourceComputeMs.HasValue) Interlocked.Add(ref _sourceComputeMs, sourceComputeMs.Value);
        if (sourceCostMicros.HasValue) Interlocked.Add(ref _sourceCostMicros, sourceCostMicros.Value);
    }
}
