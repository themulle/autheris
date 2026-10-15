namespace Autheris.Application.Data.Changes;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Relations;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// In-memory and watermark-backed implementation of <see cref="IChangeVersionProvider"/> (#77).
/// Supports seeding versions, minimum valid versions, change logs, and current table rows for unit and integration testing,
/// while delegating watermark lookups to <see cref="IMssqlWatermarkStore"/> when configured.
/// </summary>
public sealed class InMemoryChangeVersionStore : IChangeVersionProvider
{
    private readonly IMssqlWatermarkStore? _watermarkStore;
    private readonly ConcurrentDictionary<TableIdentifier, long> _currentVersions = new();
    private readonly ConcurrentDictionary<TableIdentifier, long> _minValidVersions = new();
    private readonly ConcurrentDictionary<TableIdentifier, List<SourceChangeRecord>> _changes = new();
    private readonly ConcurrentDictionary<TableIdentifier, List<IReadOnlyDictionary<string, object?>>> _currentRows = new();

    public InMemoryChangeVersionStore(IMssqlWatermarkStore? watermarkStore = null)
    {
        _watermarkStore = watermarkStore;
    }

    /// <summary>Counter tracking how many times <see cref="ReadCurrentRowsByPrimaryKeysAsync"/> was invoked (for verifying 0-row reads on 304 Not Modified).</summary>
    public int RowReadInvocationCount { get; private set; }

    public void SetTableVersion(TableIdentifier table, long currentVersion, long minValidVersion = 0)
    {
        _currentVersions[table] = currentVersion;
        _minValidVersions[table] = minValidVersion;
    }

    public void SetCurrentRows(TableIdentifier table, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        _currentRows[table] = rows
            .Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public void RecordChange(TableIdentifier table, SourceChangeRecord change, long? newCurrentVersion = null)
    {
        var list = _changes.GetOrAdd(table, _ => new List<SourceChangeRecord>());
        lock (list)
        {
            list.Add(change);
        }

        var nextVer = newCurrentVersion ?? change.ChangeVersion;
        _currentVersions.AddOrUpdate(table, nextVer, (_, existing) => Math.Max(existing, nextVer));
        _minValidVersions.TryAdd(table, 0);
    }

    public async ValueTask<long?> GetCurrentVersionAsync(TableIdentifier table, CancellationToken ct = default)
    {
        if (_currentVersions.TryGetValue(table, out var ver))
        {
            return ver;
        }

        if (_watermarkStore != null)
        {
            return await _watermarkStore.GetWatermarkAsync(table, ct).ConfigureAwait(false);
        }

        return null;
    }

    public ValueTask<long?> GetMinValidVersionAsync(TableIdentifier table, CancellationToken ct = default)
    {
        if (_minValidVersions.TryGetValue(table, out var minVer))
        {
            return ValueTask.FromResult<long?>(minVer);
        }

        return ValueTask.FromResult<long?>(0L);
    }

    public Task<IReadOnlyList<SourceChangeRecord>> GetChangesBetweenAsync(
        TableIdentifier table,
        long sinceVersion,
        long upToVersion,
        CancellationToken ct = default)
    {
        if (!_changes.TryGetValue(table, out var list))
        {
            return Task.FromResult<IReadOnlyList<SourceChangeRecord>>(Array.Empty<SourceChangeRecord>());
        }

        List<SourceChangeRecord> matching;
        lock (list)
        {
            matching = list
                .Where(c => c.ChangeVersion > sinceVersion && c.ChangeVersion <= upToVersion)
                .OrderBy(c => c.ChangeVersion)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<SourceChangeRecord>>(matching);
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCurrentRowsByPrimaryKeysAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyList<CompositeKey> keys,
        CancellationToken ct = default)
    {
        RowReadInvocationCount++;

        if (!_currentRows.TryGetValue(table, out var rows) || rows.Count == 0 || keys.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        var keySet = new HashSet<CompositeKey>(keys);
        var matched = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var row in rows)
        {
            if (CrossSourceRelationResolver.TryExtractKey(row, primaryKeyColumns, out var pk) && keySet.Contains(pk))
            {
                matched.Add(row);
            }
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(matched);
    }
}

/// <summary>
/// Issue #77: Implements lightweight governed change tokens (<c>ETag</c> / <c>If-None-Match</c>) and
/// stateless HMAC-signed <c>changesSince</c> delta cursors with RLS-exit and deleted-key disclosure policies.
/// </summary>
public sealed class GovernedChangeDetectionService : IGovernedChangeDetectionService
{
    private static readonly MaskingRule DefaultRedactRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint _pdp;
    private readonly IChangeVersionProvider _versionProvider;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ChangeDetectionOptions _options;
    private readonly GatewayOptions _gatewayOptions;
    private readonly byte[] _signingKeyBytes;

    public GovernedChangeDetectionService(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        IChangeVersionProvider versionProvider,
        IColumnMaskingProvider maskingProvider,
        IAuditLogRepository auditLogRepository,
        IOptions<ChangeDetectionOptions>? options = null,
        IOptions<GatewayOptions>? gatewayOptions = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp ?? throw new ArgumentNullException(nameof(pdp));
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _options = options?.Value ?? new ChangeDetectionOptions();
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();

        var secret = string.IsNullOrWhiteSpace(_options.SigningSecret)
            ? "autheris-default-change-detection-hmac-secret-key-2026"
            : _options.SigningSecret;
        _signingKeyBytes = Encoding.UTF8.GetBytes(secret);
    }

    public async Task<ChangeCheckResult> CheckTableModifiedAsync(
        TableIdentifier table,
        ClaimsPrincipal user,
        TenantId tenantId,
        string? ifNoneMatchToken = null,
        string? clientIp = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var metadata = await RequireTableMetadataAsync(table, ct).ConfigureAwait(false);
        await GovernedQueryStages.AuthorizeTableAsync(
            _pdp,
            table,
            metadata,
            user,
            tenantId,
            clientIp,
            requestedColumns: null,
            ct).ConfigureAwait(false);

        var currentVersion = await _versionProvider.GetCurrentVersionAsync(table, ct).ConfigureAwait(false);
        if (!currentVersion.HasValue)
        {
            return new ChangeCheckResult(
                SupportsChangeTracking: false,
                NotModified: false,
                ChangeToken: null,
                CurrentVersion: null,
                QueryCost: 0.0);
        }

        var currentToken = ComputeChangeToken(tenantId, table, currentVersion.Value);
        bool notModified = !string.IsNullOrWhiteSpace(ifNoneMatchToken) &&
                           TokensEqualConstantTime(NormalizeETag(ifNoneMatchToken), NormalizeETag(currentToken));

        return new ChangeCheckResult(
            SupportsChangeTracking: true,
            NotModified: notModified,
            ChangeToken: currentToken,
            CurrentVersion: currentVersion.Value,
            QueryCost: notModified ? 0.0 : 1.0);
    }

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

    public async Task<ChangesSinceResponse> GetChangesSinceAsync(
        ChangesSinceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.User);

        var metadata = await RequireTableMetadataAsync(request.Table, ct).ConfigureAwait(false);
        var decision = await GovernedQueryStages.AuthorizeTableAsync(
            _pdp,
            request.Table,
            metadata,
            request.User,
            request.TenantId,
            request.ClientIp,
            request.SelectColumns,
            ct).ConfigureAwait(false);

        EnsurePrimaryKeyColumnsClear(metadata, decision);

        var currentVersion = await _versionProvider.GetCurrentVersionAsync(request.Table, ct).ConfigureAwait(false)
            ?? throw new GatewaySecurityException(
                $"Table '{request.Table}' does not support change tracking.",
                ChangeDetectionErrorCodes.ChangeTrackingNotSupported);

        var minValidVersion = await _versionProvider.GetMinValidVersionAsync(request.Table, ct).ConfigureAwait(false);

        // Initial sync: when no cursor is supplied, issue a baseline cursor at currentVersion without scanning changes.
        if (string.IsNullOrWhiteSpace(request.Cursor))
        {
            var initialCursor = IssueCursor(request.Table, request.User, request.TenantId, currentVersion);
            return new ChangesSinceResponse(
                Dataset: request.Table.ToString(),
                NextCursor: initialCursor,
                SyncVersion: currentVersion,
                Upserted: Array.Empty<IReadOnlyDictionary<string, object?>>(),
                Removed: Array.Empty<IReadOnlyDictionary<string, object?>>(),
                HasMore: false);
        }

        var cursorPayload = ValidateCursor(
            request.Cursor,
            request.Table,
            request.User,
            request.TenantId,
            minValidVersion);

        var rawChanges = await _versionProvider.GetChangesBetweenAsync(
            request.Table,
            cursorPayload.SyncVersion,
            currentVersion,
            ct).ConfigureAwait(false);

        var pkColumns = metadata.PrimaryKeyColumns;
        var knownKeySet = BuildKnownKeySet(request.KnownKeys, pkColumns);

        // Consolidate net changes per primary key in chronological order
        var netChangesByKey = new Dictionary<CompositeKey, (SourceChangeRecord Latest, IReadOnlyDictionary<string, object?>? EarliestPreImage)>();
        var orderedKeys = new List<CompositeKey>();

        foreach (var change in rawChanges.OrderBy(c => c.ChangeVersion))
        {
            if (!CrossSourceRelationResolver.TryExtractKey(change.PrimaryKeyValues, pkColumns, out var pk))
            {
                continue;
            }

            if (!netChangesByKey.TryGetValue(pk, out var existing))
            {
                orderedKeys.Add(pk);
                netChangesByKey[pk] = (change, change.PreImage);
            }
            else
            {
                netChangesByKey[pk] = (change, existing.EarliestPreImage ?? change.PreImage);
            }
        }

        var candidateNonDeletedKeys = orderedKeys
            .Where(k => netChangesByKey[k].Latest.Operation != CdcOperation.Delete)
            .ToList();

        var currentRowsByKey = new Dictionary<CompositeKey, IReadOnlyDictionary<string, object?>>();
        if (candidateNonDeletedKeys.Count > 0)
        {
            var currentRows = await _versionProvider.ReadCurrentRowsByPrimaryKeysAsync(
                request.Table,
                pkColumns,
                candidateNonDeletedKeys,
                ct).ConfigureAwait(false);

            foreach (var row in currentRows)
            {
                if (CrossSourceRelationResolver.TryExtractKey(row, pkColumns, out var pk))
                {
                    currentRowsByKey[pk] = row;
                }
            }
        }

        var columnsToProject = ResolveProjectedColumns(request.SelectColumns, metadata, decision);
        var upserted = new List<IReadOnlyDictionary<string, object?>>();
        var removed = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var pk in orderedKeys)
        {
            var (latestChange, earliestPreImage) = netChangesByKey[pk];
            bool isPhysicalDelete = latestChange.Operation == CdcOperation.Delete || !currentRowsByKey.TryGetValue(pk, out var currentRow);

            if (!isPhysicalDelete && currentRowsByKey.TryGetValue(pk, out var activeRow))
            {
                if (PassesCallerGovernance(activeRow, metadata, decision, request.TenantId))
                {
                    upserted.Add(ProjectAndMaskRow(activeRow, columnsToProject, metadata, decision, request.TenantId));
                    continue;
                }

                // RLS-exit transition: the row still exists in the table, but an update moved it out of the caller's RLS scope!
                if (ShouldDiscloseRemovedKey(pk, earliestPreImage, knownKeySet, metadata, decision, request.TenantId, isRlsExit: true))
                {
                    removed.Add(BuildPrimaryKeyDictionary(pk, pkColumns));
                }

                continue;
            }

            // Physical delete in the underlying data source
            if (ShouldDiscloseRemovedKey(pk, earliestPreImage, knownKeySet, metadata, decision, request.TenantId, isRlsExit: false))
            {
                removed.Add(BuildPrimaryKeyDictionary(pk, pkColumns));
            }
        }

        var nextCursor = IssueCursor(request.Table, request.User, request.TenantId, currentVersion);

        var actorSid = request.User.GetUserSid() ?? new Sid(request.User.Identity?.Name ?? "anonymous");
        await GovernedQueryStages.RecordTableReadAsync(
            _auditLogRepository,
            request.TenantId,
            actorSid,
            request.Table,
            allowed: true,
            details: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = "changes_since",
                ["since_version"] = cursorPayload.SyncVersion,
                ["sync_version"] = currentVersion,
                ["upserted_count"] = upserted.Count,
                ["removed_count"] = removed.Count
            },
            ct).ConfigureAwait(false);

        return new ChangesSinceResponse(
            Dataset: request.Table.ToString(),
            NextCursor: nextCursor,
            SyncVersion: currentVersion,
            Upserted: upserted,
            Removed: removed,
            HasMore: false);
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
        // When the table has no RLS or virtual filter restrictions, any key within the tenant is visible.
        bool hasRowLevelFilter = !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
                                 !string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql);

        if (metadata.DeletedKeyPolicy == DeletedKeyPolicy.AllDeletedKeys)
        {
            return true;
        }

        // KnownKeysOnly policy (default):
        // 1. If the caller explicitly supplied the key in KnownKeys, disclose its removal.
        if (knownKeySet.Contains(pk))
        {
            return true;
        }

        // 2. If a pre-image is available (e.g., from an Update RLS-exit or full-row CDC log) and proves the row
        // previously satisfied the caller's tenant and RLS policy, disclose its removal.
        if (preImage != null && PassesCallerGovernance(preImage, metadata, decision, tenantId))
        {
            return true;
        }

        // 3. If no row-level filter applies and it is an RLS-exit, disclose; otherwise fail closed so foreign deleted keys are never leaked.
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
        bool maskingDisabled = _gatewayOptions.IsColumnMaskingDisabled;

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

            if (access == ColumnAccessLevel.Mask && !maskingDisabled)
            {
                var rule = metadata.ColumnMaskingRules.TryGetValue(colName, out var r) ? r : DefaultRedactRule;
                var scopedRule = GatewayExecutionService.ScopeRuleForTenant(rule, tenantId.Value, _gatewayOptions.DataMasking?.HmacKeyId);
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
