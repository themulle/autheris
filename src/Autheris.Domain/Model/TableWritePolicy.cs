using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

namespace Autheris.Domain.Model;

/// <summary>Table-level DML opt-in mode. Every table defaults to <see cref="ReadOnly"/>.</summary>
public enum TableWriteMode
{
    ReadOnly = 0,
    BoundedSingleTable = 1,
    Full = 2
}

/// <summary>Supported server-side column stamp tokens enforced by the gateway on INSERT and locked on UPDATE.</summary>
public static class TableWriteServerStampKinds
{
    public const string TenantId = "tenant_id";
    public const string CallerSid = "caller_sid";
    public const string PrimaryGroupSid = "primary_group_sid";
    public const string UtcNow = "utc_now";

    public static bool IsSupported(string? token) =>
        string.Equals(token, TenantId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, CallerSid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, PrimaryGroupSid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, UtcNow, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string token) => token.Trim().ToLowerInvariant();
}

/// <summary>
/// Explicit, tenant-scoped table write policy declaring whether and how a catalog table may accept governed DML.
/// </summary>
public sealed record TableWritePolicy(
    TenantId Tenant,
    Guid TableId,
    TableWriteMode Mode,
    IReadOnlyList<WriteOperation> AllowedOperations,
    IReadOnlyList<string>? WritableColumns = null,
    bool RequirePrimaryKeyPredicate = false,
    bool RequireRowFilter = false,
    long? MaxRowsPerStatement = null,
    IReadOnlyDictionary<string, string>? ServerStampedColumns = null,
    string UpdatedBy = "",
    DateTimeOffset UpdatedAt = default)
{
    public const long DefaultBoundedMaxRows = 100;

    public bool AllowsWrites => Mode != TableWriteMode.ReadOnly && AllowedOperations.Count > 0;

    public bool AllowsOperation(WriteOperation operation) =>
        AllowsWrites && AllowedOperations.Contains(operation);

    public bool EffectiveRequirePrimaryKeyPredicate =>
        Mode == TableWriteMode.BoundedSingleTable || RequirePrimaryKeyPredicate;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Tenant.Value) || TenantId.IsReserved(Tenant))
        {
            throw new InvalidOperationException("Table write policy requires a valid non-reserved tenant.");
        }

        if (TableId == Guid.Empty)
        {
            throw new InvalidOperationException("Table write policy requires a non-empty table identifier.");
        }

        if (!Enum.IsDefined(Mode))
        {
            throw new InvalidOperationException($"Unknown table write mode '{Mode}'.");
        }

        ArgumentNullException.ThrowIfNull(AllowedOperations);
        foreach (var op in AllowedOperations)
        {
            if (op is not (WriteOperation.Insert or WriteOperation.Update or WriteOperation.Delete))
            {
                throw new InvalidOperationException($"Unsupported table write operation '{op}'.");
            }
        }

        if (Mode == TableWriteMode.ReadOnly && AllowedOperations.Count > 0)
        {
            throw new InvalidOperationException("A ReadOnly table write policy must not declare allowed operations.");
        }

        if (Mode != TableWriteMode.ReadOnly && AllowedOperations.Count == 0)
        {
            throw new InvalidOperationException("A writable table policy must declare at least one allowed operation.");
        }

        if (WritableColumns is { Count: > 0 })
        {
            if (WritableColumns.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("WritableColumns must not contain empty or whitespace entries.");
            }

            if (WritableColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != WritableColumns.Count)
            {
                throw new InvalidOperationException("WritableColumns must be distinct (case-insensitive).");
            }
        }

        if (MaxRowsPerStatement.HasValue && MaxRowsPerStatement.Value <= 0)
        {
            throw new InvalidOperationException("MaxRowsPerStatement must be positive when specified.");
        }

        if (ServerStampedColumns is { Count: > 0 })
        {
            foreach (var (column, stampKind) in ServerStampedColumns)
            {
                if (string.IsNullOrWhiteSpace(column))
                {
                    throw new InvalidOperationException("ServerStampedColumns must not contain empty column names.");
                }

                if (!TableWriteServerStampKinds.IsSupported(stampKind))
                {
                    throw new InvalidOperationException($"Unsupported server stamp token '{stampKind}' for column '{column}'.");
                }
            }
        }
    }
}
