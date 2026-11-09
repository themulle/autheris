namespace Autheris.Application.Usage;

public readonly record struct SourceExecutionStats(
    long? SourceComputeMs,
    decimal? SourceCost,
    string? SourceCostUnit,
    string? SourceQueryId);
