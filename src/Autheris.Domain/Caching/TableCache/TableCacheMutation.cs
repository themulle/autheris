namespace Autheris.Domain.Caching.TableCache;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Represents an incremental delta mutation applied to a cached table.
/// </summary>
public sealed record TableCacheMutation(
    CdcOperation Operation,
    IReadOnlyDictionary<string, object?> PrimaryKeyValues,
    IReadOnlyDictionary<string, object?> RowValues,
    long StreamOffset,
    DateTimeOffset Timestamp)
{
    public TableCacheMutation(
        CdcStreamEventKind kind,
        IReadOnlyDictionary<string, object?> primaryKeyValues,
        IReadOnlyDictionary<string, object?> rowValues,
        long streamOffset,
        DateTimeOffset timestamp)
        : this(kind == CdcStreamEventKind.Change ? CdcOperation.Insert : (CdcOperation)(int)kind, primaryKeyValues, rowValues, streamOffset, timestamp)
    {
    }
}
