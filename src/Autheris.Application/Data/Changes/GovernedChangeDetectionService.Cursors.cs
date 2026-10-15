namespace Autheris.Application.Data.Changes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Relations;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

public sealed partial class GovernedChangeDetectionService
{
    public string IssueCursor(
        TableIdentifier table,
        ClaimsPrincipal user,
        TenantId tenantId,
        long syncVersion,
        DateTimeOffset? issuedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var subjectId = ResolveSubjectId(user);

        var payload = new ChangesSinceCursorPayload(
            TenantId: tenantId.Value,
            SubjectId: subjectId,
            TableIdentifier: table.ToString(),
            SyncVersion: syncVersion,
            IssuedAtUtc: issuedAtUtc ?? DateTimeOffset.UtcNow);

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var payloadB64 = Base64UrlEncode(jsonBytes);
        var sigB64 = ComputeHmacBase64Url(payloadB64);

        return $"{payloadB64}.{sigB64}";
    }

    public ChangesSinceCursorPayload ValidateCursor(
        string cursor,
        TableIdentifier expectedTable,
        ClaimsPrincipal user,
        TenantId tenantId,
        long? minValidVersion = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(cursor))
        {
            throw new GatewaySecurityException("Cursor must not be empty.", ChangeDetectionErrorCodes.CursorInvalid);
        }

        int dotIndex = cursor.IndexOf('.');
        if (dotIndex <= 0 || dotIndex == cursor.Length - 1 || cursor.IndexOf('.', dotIndex + 1) >= 0)
        {
            throw new GatewaySecurityException("Cursor format is invalid.", ChangeDetectionErrorCodes.CursorInvalid);
        }

        var payloadB64 = cursor[..dotIndex];
        var providedSigB64 = cursor[(dotIndex + 1)..];
        var expectedSigB64 = ComputeHmacBase64Url(payloadB64);

        if (!TokensEqualConstantTime(providedSigB64, expectedSigB64))
        {
            throw new GatewaySecurityException("Cursor signature verification failed.", ChangeDetectionErrorCodes.CursorInvalid);
        }

        ChangesSinceCursorPayload? payload;
        try
        {
            var jsonBytes = Base64UrlDecode(payloadB64);
            payload = JsonSerializer.Deserialize<ChangesSinceCursorPayload>(jsonBytes);
        }
        catch (Exception ex)
        {
            throw new GatewaySecurityException("Cursor payload could not be decoded.", ChangeDetectionErrorCodes.CursorInvalid, ex);
        }

        if (payload == null)
        {
            throw new GatewaySecurityException("Cursor payload is null.", ChangeDetectionErrorCodes.CursorInvalid);
        }

        var callerSubject = ResolveSubjectId(user);
        if (!string.Equals(payload.TenantId, tenantId.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payload.SubjectId, callerSubject, StringComparison.Ordinal))
        {
            throw new GatewaySecurityException(
                "Cursor was issued to a different principal or tenant and cannot be replayed.",
                ChangeDetectionErrorCodes.CursorCallerMismatch);
        }

        if (!string.Equals(payload.TableIdentifier, expectedTable.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewaySecurityException(
                $"Cursor was issued for table '{payload.TableIdentifier}', not '{expectedTable}'.",
                ChangeDetectionErrorCodes.CursorInvalid);
        }

        if (_options.MaxCursorAge > TimeSpan.Zero &&
            (DateTimeOffset.UtcNow - payload.IssuedAtUtc) > _options.MaxCursorAge)
        {
            throw new GatewaySecurityException(
                "Cursor has exceeded the maximum allowed retention age; perform a full re-read.",
                ChangeDetectionErrorCodes.CursorExpired);
        }

        if (minValidVersion.HasValue && payload.SyncVersion < minValidVersion.Value)
        {
            throw new GatewaySecurityException(
                $"Cursor sync version {payload.SyncVersion} is older than the minimum valid version {minValidVersion.Value}; perform a full re-read.",
                ChangeDetectionErrorCodes.CursorExpired);
        }

        return payload;
    }

    private static bool ShouldDiscloseRemovedKey(
        CompositeKey pk,
        IReadOnlyDictionary<string, object?>? preImage,
        HashSet<CompositeKey> knownKeySet,
        TableMetadata metadata,
        TableAccessDecision decision,
        TenantId tenantId,
        bool isRlsExit)
    {
        bool hasRowLevelFilter = !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
                                 !string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql);

        if (metadata.DeletedKeyPolicy == DeletedKeyPolicy.AllDeletedKeys)
        {
            return true;
        }

        if (knownKeySet.Contains(pk))
        {
            return true;
        }

        if (preImage != null && PassesCallerGovernance(preImage, metadata, decision, tenantId))
        {
            return true;
        }

        return !hasRowLevelFilter && isRlsExit;
    }

    private string ComputeChangeToken(TenantId tenantId, TableIdentifier table, long version)
    {
        var raw = $"{tenantId.Value}|{table}|{version}";
        var sig = ComputeHmacBase64Url(raw);
        return $"\"v{version}-{sig}\"";
    }

    private string ComputeHmacBase64Url(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = HMACSHA256.HashData(_signingKeyBytes, bytes);
        return Base64UrlEncode(hash);
    }

    private static string NormalizeETag(string etag)
    {
        var trimmed = etag.Trim();
        if (trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..].Trim();
        }

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed;
    }

    private static bool TokensEqualConstantTime(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    private static string ResolveSubjectId(ClaimsPrincipal user)
    {
        var sid = user.GetUserSid()?.Value;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            return sid;
        }

        var name = user.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        throw new GatewayUnauthorizedException("Authenticated principal with a subject identifier is required for cursor operations.");
    }

    private async Task<TableMetadata> RequireTableMetadataAsync(TableIdentifier table, CancellationToken ct)
    {
        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct).ConfigureAwait(false)
            ?? throw new TableNotFoundException(table);

        if (!metadata.Table.IsActive)
        {
            throw new GatewayForbiddenException($"Table '{table}' is not active.");
        }

        return metadata;
    }

    private static void EnsurePrimaryKeyColumnsClear(TableMetadata metadata, TableAccessDecision decision)
    {
        if (metadata.PrimaryKeyColumns.Count == 0)
        {
            throw new GatewaySecurityException(
                $"Table '{metadata.Identifier}' has no declared primary key columns and cannot use changesSince.",
                ChangeDetectionErrorCodes.PrimaryKeyNotClear);
        }

        foreach (var pkCol in metadata.PrimaryKeyColumns)
        {
            var col = metadata.GetColumn(pkCol);
            if (col == null || decision.GetEffectiveColumnAccess(col.ColumnName, metadata) != ColumnAccessLevel.Clear)
            {
                throw new GatewaySecurityException(
                    $"Primary key column '{pkCol}' on table '{metadata.Identifier}' must have Clear access for changesSince.",
                    ChangeDetectionErrorCodes.PrimaryKeyNotClear);
            }
        }
    }

    private static HashSet<CompositeKey> BuildKnownKeySet(
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? knownKeys,
        IReadOnlyList<string> pkColumns)
    {
        var set = new HashSet<CompositeKey>();
        if (knownKeys is null || knownKeys.Count == 0)
        {
            return set;
        }

        foreach (var keyDict in knownKeys)
        {
            if (CrossSourceRelationResolver.TryExtractKey(keyDict, pkColumns, out var pk))
            {
                set.Add(pk);
            }
        }

        return set;
    }

    private static bool PassesCallerGovernance(
        IReadOnlyDictionary<string, object?> row,
        TableMetadata metadata,
        TableAccessDecision decision,
        TenantId tenantId)
    {
        var tenantCol = metadata.TenantColumnName;
        if (tenantCol != null && row.TryGetValue(tenantCol, out var rowTenant) && rowTenant != null)
        {
            if (!string.Equals(rowTenant.ToString(), tenantId.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) &&
            !StreamingRowFilterAstEvaluator.Matches(row, decision.CombinedRowFilterSql))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql) &&
            !StreamingRowFilterAstEvaluator.Matches(row, decision.MandatoryRowPredicateSql))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> ResolveProjectedColumns(
        IReadOnlyList<string>? selectColumns,
        TableMetadata metadata,
        TableAccessDecision decision)
    {
        if (selectColumns is { Count: > 0 })
        {
            var cols = new List<string>(selectColumns.Count);
            foreach (var reqCol in selectColumns)
            {
                var col = metadata.GetColumn(reqCol)
                    ?? throw new GatewayInvalidQueryException($"Column '{reqCol}' does not exist on table '{metadata.Identifier}'.");
                if (decision.GetEffectiveColumnAccess(col.ColumnName, metadata) == ColumnAccessLevel.Deny)
                {
                    throw new ColumnNotReleasedException(col.ColumnName);
                }

                cols.Add(col.ColumnName);
            }

            return cols;
        }

        return metadata.Columns
            .Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) != ColumnAccessLevel.Deny)
            .Select(c => c.ColumnName)
            .ToList();
    }

    private IReadOnlyDictionary<string, object?> ProjectAndMaskRow(
        IReadOnlyDictionary<string, object?> rawRow,
        IReadOnlyList<string> columnsToProject,
        TableMetadata metadata,
        TableAccessDecision decision,
        TenantId tenantId)
    {
        var projected = new Dictionary<string, object?>(columnsToProject.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var colName in columnsToProject)
        {
            var access = decision.GetEffectiveColumnAccess(colName, metadata);
            if (access == ColumnAccessLevel.Deny)
            {
                continue;
            }

            if (!rawRow.TryGetValue(colName, out var val) || val is null or DBNull)
            {
                projected[colName] = null;
                continue;
            }

            if (access == ColumnAccessLevel.Mask)
            {
                var rule = metadata.ColumnMaskingRules.TryGetValue(colName, out var r) ? r : DefaultRedactRule;
                var scopedRule = GatewayExecutionService.ScopeRuleForTenant(rule, tenantId.Value, null);
                projected[colName] = _maskingProvider.MaskValue(colName, val, scopedRule);
            }
            else
            {
                projected[colName] = val;
            }
        }

        return projected;
    }

    private static IReadOnlyDictionary<string, object?> BuildPrimaryKeyDictionary(
        CompositeKey pk,
        IReadOnlyList<string> pkColumns)
    {
        var dict = new Dictionary<string, object?>(pkColumns.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pkColumns.Count; i++)
        {
            dict[pkColumns[i]] = pk.Values[i];
        }

        return dict;
    }
}
