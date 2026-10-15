namespace Autheris.Application.Streaming;

using System;
using Autheris.Domain.Common;

/// <summary>
/// The stored CDC watermark is older than the minimum change tracking version the source still retains: changes were cleaned up
/// and cannot be replayed. The poller never resets the watermark on its own; recovery is manual (delete the row of the table in
/// <c>autheris_cdc_watermarks</c> to restart at the current version, accepting the gap).
/// </summary>
public sealed class CdcWatermarkExpiredException : Exception
{
    public CdcWatermarkExpiredException(TableIdentifier table, long storedVersion, long minValidVersion)
        : base($"The CDC watermark {storedVersion} of table '{table}' is older than the minimum valid change tracking version {minValidVersion}; the missed changes cannot be replayed.")
    {
        Table = table;
        StoredVersion = storedVersion;
        MinValidVersion = minValidVersion;
    }

    public CdcWatermarkExpiredException()
        : base("The CDC watermark is older than the minimum valid change tracking version.")
    {
    }

    public CdcWatermarkExpiredException(string message)
        : base(message)
    {
    }

    public CdcWatermarkExpiredException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public TableIdentifier Table { get; }

    public long StoredVersion { get; }

    public long MinValidVersion { get; }
}

/// <summary>The CDC source cannot be polled safely because its configuration is invalid (no primary key, change tracking not enabled).</summary>
public sealed class CdcSourceConfigurationException : Exception
{
    public CdcSourceConfigurationException()
        : base("The CDC source configuration is invalid.")
    {
    }

    public CdcSourceConfigurationException(string message)
        : base(message)
    {
    }

    public CdcSourceConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
