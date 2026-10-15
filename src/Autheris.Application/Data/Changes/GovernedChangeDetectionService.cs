namespace Autheris.Application.Data.Changes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Relations;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #77: Implements lightweight governed change tokens (<c>ETag</c> / <c>If-None-Match</c>) and
/// stateless HMAC-signed <c>changesSince</c> delta cursors with RLS-exit and deleted-key disclosure policies.
/// </summary>
public sealed partial class GovernedChangeDetectionService : IGovernedChangeDetectionService
{
    private static readonly MaskingRule DefaultRedactRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };

    /// <summary>
    /// Per-process CSPRNG fallback signing key used only when <see cref="ChangeDetectionOptions.SigningSecret"/>
    /// is not explicitly configured, avoiding any predictable static key literal in source code (SA-05).
    /// </summary>
    private static readonly byte[] EphemeralProcessFallbackKey = RandomNumberGenerator.GetBytes(32);

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint _pdp;
    private readonly IChangeVersionProvider _versionProvider;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ChangeDetectionOptions _options;
    private readonly byte[] _signingKeyBytes;

    public GovernedChangeDetectionService(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        IChangeVersionProvider versionProvider,
        IColumnMaskingProvider maskingProvider,
        IAuditLogRepository auditLogRepository,
        IOptions<ChangeDetectionOptions>? options = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp ?? throw new ArgumentNullException(nameof(pdp));
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _options = options?.Value ?? new ChangeDetectionOptions();

        _signingKeyBytes = string.IsNullOrWhiteSpace(_options.SigningSecret)
            ? EphemeralProcessFallbackKey
            : Encoding.UTF8.GetBytes(_options.SigningSecret);
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
            bool isPhysicalDelete = latestChange.Operation == CdcOperation.Delete || !currentRowsByKey.TryGetValue(pk, out _);

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
}
