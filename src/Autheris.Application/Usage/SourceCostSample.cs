namespace Autheris.Application.Usage;

/// <summary>Delayed source-side execution telemetry sample returned by an <see cref="ISourceCostCollector"/>.</summary>
public sealed record SourceCostSample(
    string SourceQueryId,
    long? SourceComputeMs,
    decimal? SourceCost,
    string? CostUnit);
