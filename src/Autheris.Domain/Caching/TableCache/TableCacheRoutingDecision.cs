namespace Autheris.Domain.Caching.TableCache;

using System.Collections.Generic;

/// <summary>
/// Result of evaluating query routing against the table cache catalog.
/// </summary>
public sealed record TableCacheRoutingDecision(
    bool IsCached,
    bool CanSatisfyFully,
    string? InternalStorageTarget,
    TableCacheEntryMetadata? Metadata,
    IReadOnlyList<string>? MissingColumns = null,
    string? FallbackReason = null);
