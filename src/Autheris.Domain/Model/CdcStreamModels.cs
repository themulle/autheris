namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public enum CdcStreamStatus
{
    Active = 1,
    ActivationFailed = 2,
    Halted = 3,
    Disabled = 4
}

public enum CdcStreamEventKind
{
    Change = 1,
    WatermarkExpired = 2,
    Resync = 3,

    /// <summary>Synthesised per consumer at delivery; never stored.</summary>
    Gap = 4
}

public enum CdcTenantScope
{
    Row = 1,
    Owner = 2,
    Shared = 3,
    Unresolved = 4
}

public enum CdcKeyPurpose
{
    Payload = 1,
    Image = 2,
    Fingerprint = 3
}

public enum CdcOnWatermarkExpired
{
    Restart = 1,
    Halt = 2
}

public enum CdcStartPosition
{
    Latest = 1,
    Earliest = 2,
    FromOffset = 3
}

/// <summary>Stable activation / production failure codes of the governed CDC stream (PLAN-CDC-99 D-A3).</summary>
public static class CdcActivationCodes
{
    public const string TableUnavailable = "CDC_TABLE_UNAVAILABLE";
    public const string NoDeclaredPk = "CDC_NO_DECLARED_PK";
    public const string PkMismatch = "CDC_PK_MISMATCH";
    public const string TrackingDisabled = "CDC_TRACKING_DISABLED";
    public const string SnapshotIsolationOff = "CDC_SNAPSHOT_ISOLATION_OFF";
    public const string TenantColumnMissing = "CDC_TENANT_COLUMN_MISSING";
    public const string KeyUnavailable = "CDC_KEY_UNAVAILABLE";
    public const string WatermarkExpired = "CDC_WATERMARK_EXPIRED";
    public const string UnsupportedType = "CDC_UNSUPPORTED_TYPE";

    /// <summary>All codes, for allow-list checks (metric labels, audit details).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        TableUnavailable, NoDeclaredPk, PkMismatch, TrackingDisabled, SnapshotIsolationOff,
        TenantColumnMissing, KeyUnavailable, WatermarkExpired, UnsupportedType
    ];
}

/// <summary>Stream identity: 32 lower-case hex characters (a random UUID without dashes).</summary>
public readonly record struct CdcStreamId
{
    public CdcStreamId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("A CDC stream id must consist of exactly 32 lower-case hex characters.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static CdcStreamId New() => new(Guid.NewGuid().ToString("N"));

    public static bool TryParse(string? value, out CdcStreamId id)
    {
        if (IsValid(value))
        {
            id = new CdcStreamId(value!);
            return true;
        }

        id = default;
        return false;
    }

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != 32)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}

public sealed record CdcStreamDescriptor(
    CdcStreamId Id, string Source, TableIdentifier Table, CdcStreamStatus Status, string? ActivationCode,
    long HeadOffset, long TailOffset, long? SourceWatermark, DateTimeOffset? LastPolledAt);

/// <summary>Encrypted blob; <see cref="Data"/> is nonce | tag | ciphertext.</summary>
public sealed record CdcProtectedBlob(string DekId, byte[] Data);

public sealed record CdcStreamEventRecord(
    CdcStreamId StreamId, long Offset, long ChangeVersion, CdcStreamEventKind Kind, CdcOperation? Operation,
    CdcTenantScope TenantScope, string? TenantId,
    string? PriorTenantId /* SEC-CDC99-01: before-image tenant when it differs from the event tenant */,
    CdcProtectedBlob Payload, DateTimeOffset ProducedAt);

/// <summary>Image write; <see cref="Image"/> null means the image is deleted.</summary>
public sealed record CdcRowImageWrite(byte[] KeyFingerprint, CdcTenantScope TenantScope, string? TenantId,
    CdcProtectedBlob? Image, long Offset);

public sealed record CdcRowImageRecord(byte[] KeyFingerprint, CdcTenantScope TenantScope, string? TenantId, CdcProtectedBlob Image, long Offset);

public sealed record CdcAppendBatch(
    CdcStreamId StreamId, string Source, TableIdentifier Table, long FencingToken, long ExpectedHeadOffset,
    long NewWatermark, IReadOnlyList<CdcStreamEventRecord> Events, IReadOnlyList<CdcRowImageWrite> ImageWrites);

public sealed record CdcReadResult(long TailOffset, long HeadOffset, IReadOnlyList<CdcStreamEventRecord> Events);

/// <summary>Plaintext of a Change event (decrypted only in memory, per event).</summary>
public sealed record CdcChangePayload(
    IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After, bool BeforeUnknown, bool PayloadOversize);

/// <summary>Plaintext of a control event: never row data.</summary>
public sealed record CdcControlPayload(string Reason, long? StoredVersion, long? MinValidVersion, long? CurrentVersion);

public sealed record CdcWrappedKey(CdcStreamId StreamId, string DekId, CdcKeyPurpose Purpose, string KekId, byte[] WrappedKey,
    DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);
