namespace Autheris.Domain.Options;

using System.ComponentModel.DataAnnotations;

public sealed class DatabricksOptions
{
    /// <summary>
    /// INF-06: Maximum number of OAuth/OBO token entries held in memory before cache compaction or rejection.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int TokenCacheMaxEntries { get; init; } = 10_000;
}
