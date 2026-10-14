namespace Autheris.Domain.Options;

/// <summary>Options bound to <c>Analytics</c>.</summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Settings of the governed REST dataset API (<c>Analytics:DatasetApi</c>).</summary>
    public DatasetApiOptions DatasetApi { get; set; } = new();
}

/// <summary>Governed REST dataset API settings.</summary>
public sealed class DatasetApiOptions
{
    public const int DefaultMaxOffset = 100_000;
    public const int UpperBoundMaxOffset = 10_000_000;

    /// <summary>
    /// Largest OFFSET a single-source SQL table accepts (default 100 000, range 0-10 000 000). OFFSET cost on a database is
    /// still linear, so an explicit ceiling prevents full-table scans through the REST API.
    /// </summary>
    public int MaxOffset { get; set; } = DefaultMaxOffset;
}
