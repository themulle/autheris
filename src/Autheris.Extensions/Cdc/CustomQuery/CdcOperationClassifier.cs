namespace Autheris.Extensions.Cdc.CustomQuery;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Classifies query polled rows into discrete CDC operations (Insert, Update, Delete)
/// with soft-delete evaluation and constant-time deduplication (PLAN-CDC-101 D-4, SEC-CDC101-05).
/// </summary>
public static class CdcOperationClassifier
{
    public static CdcOperation ClassifyRow(
        IReadOnlyDictionary<string, object?> row,
        SoftDeleteDefinition? softDelete,
        IReadOnlyDictionary<string, object?>? priorImage,
        IReadOnlyList<WatermarkDimensionDefinition>? dimensions = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        // 1. Soft-Delete check
        if (softDelete != null && softDelete.Enabled && IsSoftDeleted(row, softDelete))
        {
            return CdcOperation.Delete;
        }

        // 2. Insert check (no prior image, or created_at == updated_at)
        if (priorImage == null)
        {
            // If both created_at and updated_at exist in row, compare them
            if (row.TryGetValue("created_at", out var created) && row.TryGetValue("updated_at", out var updated))
            {
                if (created != null && updated != null && !ValuesEqual(created, updated))
                {
                    if (TryParseTimestamp(created, out var dtCreated) && TryParseTimestamp(updated, out var dtUpdated))
                    {
                        if (dtUpdated > dtCreated)
                        {
                            return CdcOperation.Update;
                        }
                    }
                }
            }

            return CdcOperation.Insert;
        }

        // 3. Update check
        return CdcOperation.Update;
    }

    public static bool IsSoftDeleted(IReadOnlyDictionary<string, object?> row, SoftDeleteDefinition opt)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(opt);

        if (!row.TryGetValue(opt.Column, out var val) || val is null or DBNull)
        {
            return false;
        }

        return opt.Strategy switch
        {
            SoftDeleteStrategy.NotNullTimestamp => IsValidTimestamp(val),
            SoftDeleteStrategy.BooleanFlag => IsTruthy(val),
            SoftDeleteStrategy.StringStatus => string.Equals(val.ToString(), opt.DeletedValue, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static bool IsPayloadIdentical(
        IReadOnlyDictionary<string, object?> current,
        IReadOnlyDictionary<string, object?> prior)
    {
        if (ReferenceEquals(current, prior)) return true;
        if (current is null || prior is null) return false;
        if (current.Count != prior.Count) return false;

        foreach (var (k, v1) in current)
        {
            if (!prior.TryGetValue(k, out var v2)) return false;
            if (!ValuesEqual(v1, v2)) return false;
        }

        return true;
    }

    public static bool CompareFingerprints(byte[]? a, byte[]? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static bool CompareFingerprints(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);

    private static bool ValuesEqual(object? v1, object? v2)
    {
        if (v1 is null && v2 is null) return true;
        if (v1 is null || v2 is null) return false;

        if (v1 is byte[] b1 && v2 is byte[] b2)
        {
            return CryptographicOperations.FixedTimeEquals(b1, b2);
        }

        if (v1 is ReadOnlyMemory<byte> m1 && v2 is ReadOnlyMemory<byte> m2)
        {
            return CryptographicOperations.FixedTimeEquals(m1.Span, m2.Span);
        }

        if (Equals(v1, v2)) return true;

        // String representation comparison for timestamps / numbers across formats
        return string.Equals(v1.ToString(), v2.ToString(), StringComparison.Ordinal);
    }

    private static bool IsValidTimestamp(object val)
    {
        if (val is DateTime or DateTimeOffset) return true;
        return DateTimeOffset.TryParse(val.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool TryParseTimestamp(object val, out DateTimeOffset result)
    {
        if (val is DateTimeOffset dto)
        {
            result = dto;
            return true;
        }

        if (val is DateTime dt)
        {
            result = new DateTimeOffset(dt);
            return true;
        }

        return DateTimeOffset.TryParse(val.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static bool IsTruthy(object val) => val switch
    {
        bool b => b,
        byte b => b != 0,
        short s => s != 0,
        int i => i != 0,
        long l => l != 0,
        string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1",
        _ => false
    };
}
