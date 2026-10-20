namespace Autheris.Domain.Caching.TableCache;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>
/// Immutable metadata snapshot describing a cached table entry.
/// </summary>
public sealed record TableCacheEntryMetadata(
    TableIdentifier Table,
    TableCacheStorageTier StorageTier,
    string InternalStoragePath,
    string ActiveViewName,
    long GenerationEpoch,
    DateTimeOffset LastRefreshedAt,
    DateTimeOffset? ExpiresAt,
    long RowCount,
    long SizeInBytes,
    long? LastWatermarkOffset,
    bool IsHealthy,
    string? LastError,
    IReadOnlyList<string> CachedColumns,
    string? FilterPredicate,
    TableCacheOfflinePolicy OfflinePolicy);
