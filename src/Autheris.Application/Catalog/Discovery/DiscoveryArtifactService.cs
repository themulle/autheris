namespace Autheris.Application.Catalog.Discovery;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Security;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// Manages pre-computed discovery artifacts (upload, delete, and projection-safe sample serving)
/// with tenant isolation, owner/uploader RBAC, and fail-closed Tier A audit rollback on mutations.
/// </summary>
public sealed class DiscoveryArtifactService : IDiscoveryArtifactService
{
    private readonly ITableMetadataRepository _metadata;
    private readonly IDiscoveryArtifactStore _artifacts;
    private readonly IContainerOwnership _owners;
    private readonly IAuditWriter _auditWriter;
    private readonly IGatewayRoleEvaluator _roleEvaluator;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IOptions<CatalogDiscoveryOptions> _discoveryOptions;
    private readonly IRateLimiterService? _rateLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly IAccessDecisionPoint? _decisionPoint;
    private readonly IRestrictedScopePolicy _restrictedScopes;
    private readonly ICatalogVisibilityService? _catalogVisibility;
    private readonly Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider? _accessPolicySettings;
    private readonly IProtectionSnapshotProvider? _protectionProvider;
    private readonly IColumnMaskingProvider? _maskingProvider;

    public DiscoveryArtifactService(
        ITableMetadataRepository metadata,
        IDiscoveryArtifactStore artifacts,
        IContainerOwnership owners,
        IAuditWriter auditWriter,
        IGatewayRoleEvaluator roleEvaluator,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<CatalogDiscoveryOptions> discoveryOptions,
        IRateLimiterService? rateLimiter = null,
        TimeProvider? timeProvider = null,
        IAccessDecisionPoint? decisionPoint = null,
        IRestrictedScopePolicy? restrictedScopes = null,
        ICatalogVisibilityService? catalogVisibility = null,
        Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider? accessPolicySettings = null,
        IProtectionSnapshotProvider? protectionProvider = null,
        IColumnMaskingProvider? maskingProvider = null)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _roleEvaluator = roleEvaluator ?? throw new ArgumentNullException(nameof(roleEvaluator));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _discoveryOptions = discoveryOptions ?? throw new ArgumentNullException(nameof(discoveryOptions));
        _rateLimiter = rateLimiter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _decisionPoint = decisionPoint;
        _restrictedScopes = restrictedScopes ?? RestrictedScopePolicy.Default;
        _catalogVisibility = catalogVisibility;
        _accessPolicySettings = accessPolicySettings;
        _protectionProvider = protectionProvider;
        _maskingProvider = maskingProvider;
    }

    public async Task<DiscoveryArtifactWriteResult> UpsertAsync(
        ClaimsPrincipal? principal,
        string? datasetId,
        ReadOnlyMemory<byte> rawBody,
        string source,
        CancellationToken ct = default)
    {
        var gate = await AuthorizeMutationAsync(principal, datasetId, ct).ConfigureAwait(false);
        if (gate.Failure is { } failure)
        {
            return failure;
        }

        var table = gate.Table!;
        var settings = _discoveryOptions.Value.Discovery ?? new CatalogDiscoverySettings();
        var validation = DiscoveryArtifactValidator.ValidateRawUtf8(rawBody, table, settings);
        if (!validation.IsValid)
        {
            return new DiscoveryArtifactWriteResult(
                DiscoveryArtifactOutcome.InvalidArtifact,
                DatasetId: table.Identifier.ToString(),
                ViolationCode: validation.ViolationCode,
                Message: validation.Message);
        }

        var before = await _artifacts.GetAsync(table.Table.Id, ArtifactProvenance.Upload, ct).ConfigureAwait(false);
        var updated = new DiscoveryArtifacts(
            TableId: table.Table.Id,
            TenantId: gate.EffectiveTenant,
            SamplesJson: validation.SamplesJson,
            StatisticsJson: validation.StatisticsJson,
            ContentSha256: validation.ContentSha256,
            UploadedBy: gate.ActorSid.Value,
            UploadedAt: _timeProvider.GetUtcNow(),
            SchemaVersion: DiscoveryArtifactValidator.SupportedSchemaVersion,
            Provenance: ArtifactProvenance.Upload);

        await _artifacts.UpsertAsync(updated, gate.ActorSid, ct).ConfigureAwait(false);

        try
        {
            await RecordChangeAuditAsync(
                tenantId: gate.CallerTenant,
                actorSid: gate.ActorSid,
                datasetId: table.Identifier.ToString(),
                operation: "upsert",
                source: source,
                beforeSha256: before?.ContentSha256,
                afterSha256: updated.ContentSha256,
                sampleRowCount: validation.SampleRowCount,
                statisticsColumnCount: validation.StatisticsColumnCount,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RestoreBeforeAsync(table.Table.Id, ArtifactProvenance.Upload, before, gate.ActorSid).ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new DiscoveryArtifactWriteResult(
                DiscoveryArtifactOutcome.Unavailable,
                Message: "Audit persistence failed; artifact update was rolled back.");
        }

        return new DiscoveryArtifactWriteResult(
            DiscoveryArtifactOutcome.Ok,
            DatasetId: table.Identifier.ToString(),
            ContentSha256: updated.ContentSha256,
            SampleRowCount: validation.SampleRowCount,
            StatisticsColumnCount: validation.StatisticsColumnCount);
    }

    public async Task<DiscoveryArtifactWriteResult> DeleteAsync(
        ClaimsPrincipal? principal,
        string? datasetId,
        string source,
        CancellationToken ct = default)
    {
        var gate = await AuthorizeMutationAsync(principal, datasetId, ct).ConfigureAwait(false);
        if (gate.Failure is { } failure)
        {
            return failure;
        }

        var before = await _artifacts.GetAsync(gate.Table!.Table.Id, ArtifactProvenance.Upload, ct).ConfigureAwait(false);
        if (before is null)
        {
            return new DiscoveryArtifactWriteResult(
                DiscoveryArtifactOutcome.NoContent,
                DatasetId: gate.Table.Identifier.ToString());
        }

        await _artifacts.DeleteAsync(gate.Table.Table.Id, ArtifactProvenance.Upload, gate.ActorSid, ct).ConfigureAwait(false);

        try
        {
            await RecordChangeAuditAsync(
                tenantId: gate.CallerTenant,
                actorSid: gate.ActorSid,
                datasetId: gate.Table.Identifier.ToString(),
                operation: "delete",
                source: source,
                beforeSha256: before.ContentSha256,
                afterSha256: null,
                sampleRowCount: 0,
                statisticsColumnCount: 0,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RestoreBeforeAsync(gate.Table.Table.Id, ArtifactProvenance.Upload, before, gate.ActorSid).ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new DiscoveryArtifactWriteResult(
                DiscoveryArtifactOutcome.Unavailable,
                Message: "Audit persistence failed; artifact deletion was rolled back.");
        }

        return new DiscoveryArtifactWriteResult(
            DiscoveryArtifactOutcome.NoContent,
            DatasetId: gate.Table.Identifier.ToString());
    }

    public async Task<DiscoverySamplesReadResult> GetSamplesAsync(
        ClaimsPrincipal? principal,
        string? datasetId,
        int? limit = null,
        DiscoverySurface surface = DiscoverySurface.Rest,
        CancellationToken ct = default)
    {
        if (principal is null)
        {
            return new DiscoverySamplesReadResult(
                DiscoveryArtifactOutcome.Unauthenticated,
                null,
                "UNAUTHENTICATED",
                "Authentication is required.");
        }

        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Value.Tenancy);
        var resolution = CallerScopeResolver.ResolutionOf(principal, catalogOwner);
        var tenantId = resolution.Tenant ?? default;

        var channel = surface == DiscoverySurface.GraphQl ? CatalogChannel.GraphQl : CatalogChannel.Rest;
        var caller = new CatalogCaller(principal, tenantId, channel);
        var settings = _discoveryOptions.Value.Discovery ?? new CatalogDiscoverySettings();

        Sid actorSid;
        try
        {
            actorSid = await DiscoveryCallerGate.EnsureAllowedAsync(
                caller,
                _discoveryOptions.Value,
                _gatewayOptions.Value,
                _rateLimiter,
                ct).ConfigureAwait(false);
        }
        catch (DiscoveryStatusException ex)
        {
            return ex.Kind switch
            {
                DiscoveryFailureKind.Unauthenticated => new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Unauthenticated, null, ex.Code, ex.Message),
                DiscoveryFailureKind.Forbidden => new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Forbidden, null, ex.Code, ex.Message),
                _ => new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Unavailable, null, ex.Code, ex.Message)
            };
        }

        var unavailablePayload = new DiscoverySamplesResponse(Available: false, Samples: []);
        var identifier = await DatasetIdResolver.ResolveActiveAsync(datasetId, _metadata, ct).ConfigureAwait(false);
        if (identifier is not { } id)
        {
            return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
        }

        var table = await _metadata.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
        if (table is null || !table.Table.IsActive || table.Table.DiscoveryHidden || table.Table.SamplesDisabled)
        {
            return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
        }

        var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Value.Catalog.SharedTables);
        if (!IsTableInCallerTenantScope(table, resolution.Tenant!.Value, catalogOwner, sharedTables))
        {
            return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
        }

        var allArtifacts = await _artifacts.GetAllAsync(table.Table.Id, ct).ConfigureAwait(false);
        var importArtifact = allArtifacts.FirstOrDefault(a =>
            a.Provenance == ArtifactProvenance.Import && !string.IsNullOrWhiteSpace(a.SamplesJson));
        var dataArtifact = allArtifacts.FirstOrDefault(a =>
                a.Provenance == ArtifactProvenance.Upload && !string.IsNullOrWhiteSpace(a.SamplesJson))
            ?? allArtifacts.FirstOrDefault(a =>
                a.Provenance != ArtifactProvenance.Import && !string.IsNullOrWhiteSpace(a.SamplesJson));

        var maxRows = Math.Clamp(settings.MaxSampleRows, 1, CatalogDiscoverySettings.HardMaxSampleRows);
        var take = limit is > 0 ? Math.Min(limit.Value, maxRows) : maxRows;

        TableAccessDecision? decision = null;
        if (_decisionPoint is not null && (dataArtifact is not null || importArtifact is not null))
        {
            var subject = SecurityPrincipalContext.FromPrincipal(principal, resolution.Tenant!.Value);
            decision = await _decisionPoint.DecideAsync(
                new AccessRequest(subject, table, AccessOperation.Discover, AccessTransport.Discovery),
                ct).ConfigureAwait(false);
        }

        if (dataArtifact is not null && decision is { IsAllowed: true })
        {
            var visible = _catalogVisibility is null
                ? table
                : (await _catalogVisibility.FindVisibleAsync(caller, id, ct).ConfigureAwait(false))?.Table;
            if (visible is null)
            {
                return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
            }

            var (protOk, protection) = await TryLoadProtectionAsync(resolution.Tenant!.Value, id, ct).ConfigureAwait(false);
            if (!protOk)
            {
                return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
            }

            var projected = ProjectVisibleSamples(dataArtifact.SamplesJson!, visible, take, decision, protection);
            if (projected.Count == 0)
            {
                return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
            }

            await RecordSampleReadAuditBestEffortAsync(resolution.Tenant!.Value, actorSid, id.ToString(), projected.Count, surface, ct).ConfigureAwait(false);
            return new DiscoverySamplesReadResult(
                DiscoveryArtifactOutcome.Ok,
                new DiscoverySamplesResponse(Available: true, Samples: projected));
        }

        if (importArtifact is not null)
        {
            if (decision is { IsAllowed: false, DenialKind: TableAccessDenialKind.ConsentDeny })
            {
                return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
            }

            var policy = _accessPolicySettings is null
                ? null
                : await TryGetPolicyAsync(resolution.Tenant!.Value, ct).ConfigureAwait(false);
            if (_accessPolicySettings is not null && policy is null)
            {
                return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
            }

            var serveImport = (policy?.ServeImportSamples ?? true) &&
                !(policy?.QuarantineSuspectedPiiSamples == true && CatalogPiiDetector.JsonHasSuspectedPii(importArtifact.SamplesJson));
            if (serveImport)
            {
                var visible = _catalogVisibility is null
                    ? table
                    : (await _catalogVisibility.FindVisibleAsync(caller, id, ct).ConfigureAwait(false))?.Table;
                if (visible is null)
                {
                    return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
                }

                var (protOk, protection) = await TryLoadProtectionAsync(resolution.Tenant!.Value, id, ct).ConfigureAwait(false);
                if (!protOk)
                {
                    return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
                }

                var importProjected = decision is { IsAllowed: true }
                    ? ProjectVisibleSamples(importArtifact.SamplesJson!, visible, take, decision, protection)
                    : ProjectCatalogImportSamples(importArtifact.SamplesJson!, visible, take, principal, decision, protection);
                if (importProjected.Count > 0)
                {
                    await RecordSampleReadAuditBestEffortAsync(resolution.Tenant!.Value, actorSid, id.ToString(), importProjected.Count, surface, ct).ConfigureAwait(false);
                    return new DiscoverySamplesReadResult(
                        DiscoveryArtifactOutcome.Ok,
                        new DiscoverySamplesResponse(Available: true, Samples: importProjected));
                }
            }
        }

        return new DiscoverySamplesReadResult(DiscoveryArtifactOutcome.Ok, unavailablePayload);
    }

    private async Task<(bool Ok, EffectiveProtection? Protection)> TryLoadProtectionAsync(
        TenantId tenantId,
        TableIdentifier id,
        CancellationToken ct)
    {
        if (_protectionProvider is null)
        {
            return (true, null);
        }

        try
        {
            return (true, await _protectionProvider.GetAsync(tenantId, id, ct).ConfigureAwait(false));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    private async Task<TenantAccessPolicy?> TryGetPolicyAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            return await _accessPolicySettings!.GetEffectiveAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record MutationGate(
        DiscoveryArtifactWriteResult? Failure,
        TableMetadata? Table,
        Sid ActorSid,
        TenantId CallerTenant,
        string EffectiveTenant);

    private async Task<MutationGate> AuthorizeMutationAsync(ClaimsPrincipal? principal, string? datasetId, CancellationToken ct)
    {
        if (principal?.Identity?.IsAuthenticated != true || principal.GetUserSid() is not { } actorSid ||
            string.IsNullOrWhiteSpace(actorSid.Value) ||
            string.Equals(actorSid.Value, DiscoveryCallerGate.AnonymousMcpClientSid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actorSid.Value, DiscoveryCallerGate.AnonymousSidValue, StringComparison.OrdinalIgnoreCase))
        {
            return new MutationGate(
                new DiscoveryArtifactWriteResult(DiscoveryArtifactOutcome.Unauthenticated, Message: "Authentication is required."),
                null, default, default, string.Empty);
        }

        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Value.Tenancy);
        var resolution = CallerScopeResolver.ResolutionOf(principal, catalogOwner);
        var scope = CallerScopeResolver.Resolve(principal, resolution, catalogOwner);
        if (scope == CallerScope.None || resolution.Tenant is not { } callerTenant)
        {
            return new MutationGate(NotFoundResult(), null, actorSid, default, string.Empty);
        }

        var resolvedId = await DatasetIdResolver.ResolveActiveAsync(datasetId, _metadata, ct).ConfigureAwait(false);
        var table = resolvedId is { } id
            ? await _metadata.GetTableMetadataAsync(id, ct).ConfigureAwait(false)
            : null;

        if (table is null || !table.Table.IsActive)
        {
            return new MutationGate(NotFoundResult(), null, actorSid, callerTenant, string.Empty);
        }

        if (scope != CallerScope.Global && !BelongsToTenantForMutation(table, callerTenant, catalogOwner))
        {
            return new MutationGate(NotFoundResult(), null, actorSid, callerTenant, string.Empty);
        }

        var isTenantAdmin = scope is CallerScope.Global or CallerScope.CatalogOwner or CallerScope.Tenant;
        var isOwner = !isTenantAdmin &&
            await _owners.IsAuthorizedApproverForTableAsync(table.Identifier, actorSid, ct).ConfigureAwait(false);

        if (table.Table.DiscoveryHidden && !isTenantAdmin && !isOwner)
        {
            return new MutationGate(NotFoundResult(), null, actorSid, callerTenant, string.Empty);
        }

        var hasUploaderRole = isTenantAdmin || isOwner ||
            _roleEvaluator.HasRole(principal, GatewayRole.CatalogArtifactUploader, callerTenant.Value);

        if (!hasUploaderRole)
        {
            return new MutationGate(
                new DiscoveryArtifactWriteResult(
                    DiscoveryArtifactOutcome.Forbidden,
                    Message: "Caller is not authorized to modify discovery artifacts for this dataset."),
                null, actorSid, callerTenant, string.Empty);
        }

        var effectiveTenant = !string.IsNullOrWhiteSpace(table.OwnerTenantId)
            ? table.OwnerTenantId!
            : (catalogOwner?.Value ?? callerTenant.Value);

        return new MutationGate(null, table, actorSid, callerTenant, effectiveTenant);
    }

    private static bool BelongsToTenantForMutation(TableMetadata table, TenantId callerTenant, TenantId? catalogOwner)
    {
        if (SemanticVirtualTables.IsInSemanticDomain(table.Identifier))
        {
            return SemanticVirtualTables.BelongsToTenant(table, callerTenant);
        }

        if (!string.IsNullOrWhiteSpace(table.OwnerTenantId))
        {
            return string.Equals(table.OwnerTenantId, callerTenant.Value, StringComparison.OrdinalIgnoreCase);
        }

        return CallerScopeResolver.IsOwner(callerTenant, catalogOwner);
    }

    private static bool IsTableInCallerTenantScope(
        TableMetadata table,
        TenantId callerTenant,
        TenantId? catalogOwner,
        IReadOnlySet<TableIdentifier> sharedTables)
    {
        if (SemanticVirtualTables.IsInSemanticDomain(table.Identifier))
        {
            return SemanticVirtualTables.BelongsToTenant(table, callerTenant);
        }

        if (!string.IsNullOrWhiteSpace(table.OwnerTenantId))
        {
            return string.Equals(table.OwnerTenantId, callerTenant.Value, StringComparison.OrdinalIgnoreCase);
        }

        return CallerScopeResolver.IsOwner(callerTenant, catalogOwner) || sharedTables.Contains(table.Identifier);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ProjectCatalogImportSamples(
        string samplesJson,
        TableMetadata table,
        int maxRows,
        ClaimsPrincipal principal,
        TableAccessDecision? decision,
        EffectiveProtection? protection)
    {
        var restrictedCaller = _restrictedScopes.HeldBy(TokenAccessScope.GetScopeSet(principal.Claims)).Count > 0;
        var explicitConstraints = ExtractExplicitColumnConstraints(decision);
        var visibleColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maskColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in table.Columns)
        {
            if (col.DiscoveryHidden)
            {
                continue;
            }

            ColumnAccessLevel? explicitLevel = explicitConstraints.TryGetValue(col.ColumnName, out var lvl) ? lvl : null;
            if (explicitLevel == ColumnAccessLevel.Deny
                || (table.ColumnMaskingRules.TryGetValue(col.ColumnName, out var rule)
                    && string.Equals(rule.RuleType, "DENY", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (restrictedCaller && IsRestrictedImportColumn(table, col, explicitLevel, protection))
            {
                continue;
            }

            visibleColumns.TryAdd(col.ColumnName, col.ColumnName);
            if (explicitLevel == ColumnAccessLevel.Mask)
            {
                maskColumns.Add(col.ColumnName);
            }
        }

        if (visibleColumns.Count == 0)
        {
            return [];
        }

        return ExtractProjectedRows(samplesJson, maxRows, visibleColumns, maskColumns.Count > 0 ? maskColumns : null, table);
    }

    private static Dictionary<string, ColumnAccessLevel> ExtractExplicitColumnConstraints(TableAccessDecision? decision)
    {
        var result = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        if (decision is null)
        {
            return result;
        }

        foreach (var reason in decision.Reasons)
        {
            if (reason.Step != DecisionStep.ExplicitDeny || string.IsNullOrWhiteSpace(reason.Column))
            {
                continue;
            }

            if (string.Equals(reason.Code, "COLUMN_DENY", StringComparison.Ordinal))
            {
                result[reason.Column] = ColumnAccessLevel.Deny;
            }
            else if (string.Equals(reason.Code, "COLUMN_MASK", StringComparison.Ordinal)
                     && (!result.TryGetValue(reason.Column, out var existing) || existing != ColumnAccessLevel.Deny))
            {
                result[reason.Column] = ColumnAccessLevel.Mask;
            }
        }

        if (!decision.IsAllowed && decision.Grants.Count == 0)
        {
            foreach (var (column, level) in decision.ColumnAccess)
            {
                if (level == ColumnAccessLevel.Deny)
                {
                    result[column] = ColumnAccessLevel.Deny;
                }
                else if (level == ColumnAccessLevel.Mask
                         && (!result.TryGetValue(column, out var existing) || existing != ColumnAccessLevel.Deny))
                {
                    result[column] = ColumnAccessLevel.Mask;
                }
            }
        }

        return result;
    }

    private static bool IsRestrictedImportColumn(
        TableMetadata table,
        TableColumn col,
        ColumnAccessLevel? explicitLevel,
        EffectiveProtection? protection)
    {
        if (explicitLevel == ColumnAccessLevel.Mask
            || ClassificationMetadata.IsColumnSensitive(table, col)
            || table.ColumnMaskingRules.ContainsKey(col.ColumnName))
        {
            return true;
        }

        if (protection is null)
        {
            return false;
        }

        if (protection.Columns.TryGetValue(col.ColumnName, out var cp))
        {
            return !string.IsNullOrWhiteSpace(cp.EffectiveMaskCategory)
                || cp.EffectiveClass >= ProtectionClass.Confidential;
        }

        return !string.IsNullOrWhiteSpace(protection.TableMaskCategory);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ProjectVisibleSamples(
        string samplesJson,
        TableMetadata table,
        int maxRows,
        TableAccessDecision decision,
        EffectiveProtection? protection)
    {
        var visibleColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maskColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in table.Columns)
        {
            if (col.DiscoveryHidden)
            {
                continue;
            }

            var access = decision.GetEffectiveColumnAccess(col.ColumnName, table);
            if (access == ColumnAccessLevel.Deny)
            {
                continue;
            }

            var (mappedState, _) = DiscoveryAccessMapper.MapColumnState(table, decision, col.ColumnName);
            if (!DiscoveryAccessMapper.IsSampleVisibleState(mappedState))
            {
                continue;
            }

            if (decision.ScopeClearance is not null && mappedState != DiscoveryColumnState.Clear)
            {
                continue;
            }

            var hasMaskCategory = protection is not null && !string.IsNullOrWhiteSpace(
                protection.Columns.TryGetValue(col.ColumnName, out var cp)
                    ? cp.EffectiveMaskCategory
                    : protection.TableMaskCategory);

            if (mappedState == DiscoveryColumnState.Masked && _maskingProvider is null && _protectionProvider is null)
            {
                continue;
            }

            visibleColumns.TryAdd(col.ColumnName, col.ColumnName);
            if (mappedState == DiscoveryColumnState.Masked || hasMaskCategory)
            {
                maskColumns.Add(col.ColumnName);
            }
        }

        if (visibleColumns.Count == 0)
        {
            return [];
        }

        return ExtractProjectedRows(samplesJson, maxRows, visibleColumns, maskColumns, table);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ExtractProjectedRows(
        string samplesJson,
        int maxRows,
        IReadOnlyDictionary<string, string> visibleColumns,
        IReadOnlySet<string>? maskColumns,
        TableMetadata table)
    {
        try
        {
            using var doc = JsonDocument.Parse(samplesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<IReadOnlyDictionary<string, object?>>(Math.Min(maxRows, doc.RootElement.GetArrayLength()));
            foreach (var rowElem in doc.RootElement.EnumerateArray())
            {
                if (result.Count >= maxRows)
                {
                    break;
                }

                if (rowElem.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var projected = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in rowElem.EnumerateObject())
                {
                    if (!visibleColumns.TryGetValue(prop.Name, out var canonicalName))
                    {
                        continue;
                    }

                    if (TryExtractScalar(prop.Value, out var scalar))
                    {
                        projected[canonicalName] = maskColumns is not null && maskColumns.Contains(canonicalName)
                            ? MaskSampleScalar(canonicalName, scalar, table)
                            : scalar;
                    }
                }

                if (projected.Count > 0)
                {
                    result.Add(projected);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private object? MaskSampleScalar(string columnName, object? scalar, TableMetadata table)
    {
        if (scalar is null)
        {
            return null;
        }

        if (table.ColumnMaskingRules.TryGetValue(columnName, out var rule))
        {
            return (_maskingProvider ?? DefaultMaskingProvider).MaskValue(columnName, scalar, rule);
        }

        return (_maskingProvider ?? DefaultMaskingProvider).MaskValue(
            columnName,
            scalar,
            new MaskingRule { RuleType = "REDACT", Replacement = "***" });
    }

    private static readonly IColumnMaskingProvider DefaultMaskingProvider =
        new Autheris.Application.Services.ColumnMaskingProvider(Options.Create(new GatewayOptions()));

    private static bool TryExtractScalar(JsonElement value, out object? scalar)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                scalar = null;
                return true;
            case JsonValueKind.True:
                scalar = true;
                return true;
            case JsonValueKind.False:
                scalar = false;
                return true;
            case JsonValueKind.String:
                scalar = DiscoveryTextSanitizer.SanitizeText(value.GetString(), DiscoveryArtifactValidator.MaxScalarStringLength);
                return true;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var i64))
                {
                    scalar = i64;
                    return true;
                }

                if (value.TryGetDecimal(out var dec))
                {
                    scalar = dec;
                    return true;
                }

                if (value.TryGetDouble(out var dbl) && double.IsFinite(dbl))
                {
                    scalar = dbl;
                    return true;
                }

                scalar = null;
                return false;
            default:
                scalar = null;
                return false;
        }
    }

    private Task RecordChangeAuditAsync(
        TenantId tenantId,
        Sid actorSid,
        string datasetId,
        string operation,
        string source,
        string? beforeSha256,
        string? afterSha256,
        int sampleRowCount,
        int statisticsColumnCount,
        CancellationToken ct)
    {
        var entry = new AuditLogEntry
        {
            TenantId = tenantId,
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorSid = actorSid,
            EventType = AuditEventTypes.CatalogDiscoveryArtifactsChanged,
            TargetTable = datasetId,
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                operation,
                source,
                beforeSha256,
                afterSha256,
                sampleRowCount,
                statisticsColumnCount
            })
        };

        return _auditWriter.RecordAsync(entry, ct);
    }

    private async Task RecordSampleReadAuditBestEffortAsync(
        TenantId tenantId,
        Sid actorSid,
        string datasetId,
        int rowCount,
        DiscoverySurface surface,
        CancellationToken ct)
    {
        try
        {
            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                OccurredAt = _timeProvider.GetUtcNow(),
                ActorSid = actorSid,
                EventType = AuditEventTypes.MetadataSampleRead,
                TargetTable = datasetId,
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    surface = surface.ToString().ToLowerInvariant(),
                    rowCount
                })
            };

            await _auditWriter.RecordAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort policy for sample metadata reads.
        }
    }

    private async Task RestoreBeforeAsync(Guid tableId, ArtifactProvenance provenance, DiscoveryArtifacts? before, Sid actorSid)
    {
        try
        {
            if (before is null)
            {
                await _artifacts.DeleteAsync(tableId, provenance, actorSid, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _artifacts.UpsertAsync(before, actorSid, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Swallow secondary rollback exception so fail-closed audit result surfaces cleanly.
        }
    }

    private static DiscoveryArtifactWriteResult NotFoundResult() =>
        new(DiscoveryArtifactOutcome.NotFound, Message: "The dataset was not found.");
}
