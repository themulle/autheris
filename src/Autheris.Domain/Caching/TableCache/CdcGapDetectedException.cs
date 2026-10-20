namespace Autheris.Domain.Caching.TableCache;

using System;
using Autheris.Domain.Common;

/// <summary>
/// Thrown when a sequence gap or discontinuity is detected on a CDC change stream.
/// </summary>
public sealed class CdcGapDetectedException : Exception
{
    public TableIdentifier Table { get; }
    public long ExpectedOffset { get; }
    public long ActualOffset { get; }

    public CdcGapDetectedException(TableIdentifier table, long expectedOffset, long actualOffset)
        : base($"CDC stream sequence gap detected for table '{table}'. Expected offset: {expectedOffset}, actual offset: {actualOffset}.")
    {
        Table = table;
        ExpectedOffset = expectedOffset;
        ActualOffset = actualOffset;
    }

    public CdcGapDetectedException(string message) : base(message)
    {
    }

    public CdcGapDetectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
