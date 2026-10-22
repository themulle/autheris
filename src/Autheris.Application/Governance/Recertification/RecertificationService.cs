namespace Autheris.Application.Governance.Recertification;

using System;
using System.Collections.Generic;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Policies;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class RecertificationService : IRecertificationService
{
    private readonly IRecertificationRepository _repository;
    private readonly IFunctionalRightRepository? _functionalRightRepository;
    private readonly IPolicyEpochService? _epochService;
    private readonly IAuditWriter? _auditWriter;
    private readonly ILogger<RecertificationService>? _logger;

    public RecertificationService(
        IRecertificationRepository repository,
        IFunctionalRightRepository? functionalRightRepository = null,
        IPolicyEpochService? epochService = null,
        IAuditWriter? auditWriter = null,
        ILogger<RecertificationService>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _functionalRightRepository = functionalRightRepository;
        _epochService = epochService;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    public async Task<RecertificationCampaign> CreateCampaignAsync(
        TenantId tenantId,
        string name,
        RecertClass recertClass,
        DateTimeOffset? deadline = null,
        string? assetType = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!string.IsNullOrWhiteSpace(assetType) &&
            (string.Equals(assetType, "AccessPolicy", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(assetType, "ACCESS_POLICIES", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("UNSUPPORTED_ASSET_TYPE", nameof(assetType));
        }

        var now = DateTimeOffset.UtcNow;
        var campaignDeadline = deadline ?? now.AddDays(14); // 14-day completion deadline

        var campaign = new RecertificationCampaign(
            TenantId: tenantId.Value,
            CampaignId: Guid.NewGuid().ToString("N"),
            Name: name,
            RecertClass: recertClass,
            Status: RecertCampaignStatus.Active,
            Deadline: campaignDeadline,
            CreatedAt: now
        );

        await _repository.CreateCampaignAsync(campaign, ct).ConfigureAwait(false);

        if (_auditWriter != null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "SECURITY_RECERT_CAMPAIGN_LAUNCHED",
                ActorSid = new Sid("RECERT_GOVERNOR"),
                TargetTable = "RECERT_CAMPAIGNS",
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    campaign_id = campaign.CampaignId,
                    name = campaign.Name,
                    recert_class = campaign.RecertClass.ToString(),
                    deadline = campaign.Deadline
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }

        return campaign;
    }

    public Task<IReadOnlyList<RecertificationCampaign>> GetCampaignsAsync(TenantId tenantId, CancellationToken ct = default) =>
        _repository.GetCampaignsAsync(tenantId, ct);

    public Task<RecertificationCampaign?> GetCampaignByIdAsync(TenantId tenantId, string campaignId, CancellationToken ct = default) =>
        _repository.GetCampaignByIdAsync(tenantId, campaignId, ct);

    public async Task<RecertificationItem> AddItemAsync(
        TenantId tenantId,
        string campaignId,
        string assetType,
        string assetKey,
        string assignedTo,
        string reviewerId,
        CancellationToken ct = default)
    {
        if (string.Equals(assetType, "AccessPolicy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assetType, "ACCESS_POLICIES", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("UNSUPPORTED_ASSET_TYPE", nameof(assetType));
        }

        var item = new RecertificationItem(
            TenantId: tenantId.Value,
            CampaignId: campaignId,
            ItemId: Guid.NewGuid().ToString("N"),
            AssetType: assetType,
            AssetKey: assetKey,
            AssignedTo: assignedTo,
            ReviewerId: reviewerId,
            Decision: RecertDecision.Pending
        );

        await _repository.CreateItemAsync(item, ct).ConfigureAwait(false);
        return item;
    }

    public Task<IReadOnlyList<RecertificationItem>> GetItemsAsync(TenantId tenantId, string campaignId, CancellationToken ct = default) =>
        _repository.GetItemsAsync(tenantId, campaignId, ct);

    public async Task CertifyItemAsync(
        TenantId tenantId,
        string campaignId,
        string itemId,
        string reviewerId,
        string? comments = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);

        var item = await _repository.GetItemByIdAsync(tenantId, campaignId, itemId, ct).ConfigureAwait(false);
        if (item == null)
        {
            throw new KeyNotFoundException($"Recertification item '{itemId}' not found.");
        }

        // SEC-SC-15: Anti-Self-Review Rule
        if (string.Equals(reviewerId.Trim(), item.AssignedTo.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Self-certification prohibited: Reviewer '{reviewerId}' cannot certify their own assignment (SEC-SC-15: self_certification_prohibited).");
        }

        await _repository.UpdateItemDecisionAsync(
            tenantId, campaignId, itemId, RecertDecision.Certified, reviewerId, comments, ct).ConfigureAwait(false);
    }

    public async Task RevokeItemAsync(
        TenantId tenantId,
        string campaignId,
        string itemId,
        string reviewerId,
        string? comments = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);

        var item = await _repository.GetItemByIdAsync(tenantId, campaignId, itemId, ct).ConfigureAwait(false);
        if (item == null)
        {
            throw new KeyNotFoundException($"Recertification item '{itemId}' not found.");
        }

        // Security review: the access is removed first and the decision recorded afterwards. When the deactivation fails (or the asset
        // type is unknown) the item stays Pending and can be retried; it is never shown as Revoked while the access still exists.
        await DeactivateUnderlyingAssetAsync(new TenantId(item.TenantId), item.AssetType, item.AssetKey, ct).ConfigureAwait(false);

        await _repository.UpdateItemDecisionAsync(
            tenantId, campaignId, itemId, RecertDecision.Revoked, reviewerId, comments, ct).ConfigureAwait(false);
    }

    public async Task<int> ExecuteAutoRevocationCycleAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        var expiredPending = await _repository.GetPendingItemsPastDeadlineAsync(asOf, ct).ConfigureAwait(false);
        int revokedCount = 0;

        foreach (var item in expiredPending)
        {
            var tenantId = new TenantId(item.TenantId);

            // Security review: per-item isolation. A failing item stays Pending (retried in the next cycle) and does not stop the
            // revocation of the remaining items.
            try
            {
                await AutoRevokeItemAsync(tenantId, item, asOf, ct).ConfigureAwait(false);
                revokedCount++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Recertification auto-revocation failed for item {ItemId} of campaign {CampaignId} (tenant {TenantId}, asset type {AssetType}); the item stays pending and is retried in the next cycle.",
                    item.ItemId, item.CampaignId, item.TenantId, item.AssetType);
            }
        }

        return revokedCount;
    }

    private async Task AutoRevokeItemAsync(TenantId tenantId, RecertificationItem item, DateTimeOffset asOf, CancellationToken ct)
    {
        // 1. Deactivate the underlying permission first: the item is only marked Revoked once the access is gone.
        await DeactivateUnderlyingAssetAsync(tenantId, item.AssetType, item.AssetKey, ct).ConfigureAwait(false);

        // 2. Mark as Revoked by SYSTEM_AUTO_REVOKE
        await _repository.UpdateItemDecisionAsync(
            tenantId, item.CampaignId, item.ItemId, RecertDecision.Revoked, "SYSTEM_AUTO_REVOKE",
            "Automated revocation fail-closed upon 14-day deadline expiry", ct).ConfigureAwait(false);

        // 3. Emit Tier-A audit event
        if (_auditWriter != null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "SECURITY_RECERT_AUTO_REVOKED",
                ActorSid = new Sid("SYSTEM_AUTO_REVOKE"),
                TargetTable = item.AssetType,
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    campaign_id = item.CampaignId,
                    item_id = item.ItemId,
                    asset_type = item.AssetType,
                    asset_key = item.AssetKey,
                    assigned_to = item.AssignedTo,
                    revoked_at = asOf,
                    reason = "deadline_expired"
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes the access behind a recertification item. Fails closed: an unknown asset type, a malformed asset key or a missing
    /// repository throws instead of silently doing nothing, so the caller never records Revoked while the access still exists.
    /// An access policy that no longer exists counts as removed.
    /// </summary>
    private async Task DeactivateUnderlyingAssetAsync(TenantId tenantId, string assetType, string assetKey, CancellationToken ct)
    {
        if (string.Equals(assetType, "FunctionalRightMapping", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assetType, "FUNCTIONAL_RIGHT_MAPPINGS", StringComparison.OrdinalIgnoreCase))
        {
            // assetKey format: "realmId:groupOrRole:functionalRight"
            var parts = (assetKey ?? string.Empty).Split(':');
            if (parts.Length != 3)
            {
                throw new InvalidOperationException(
                    $"Recertification asset key '{assetKey}' of type '{assetType}' is malformed (expected 'realmId:groupOrRole:functionalRight'); the access was not revoked.");
            }

            if (_functionalRightRepository == null)
            {
                throw new InvalidOperationException(
                    $"No functional right repository is configured; the recertification asset '{assetKey}' of type '{assetType}' cannot be revoked.");
            }

            var realmId = parts[0];
            var groupOrRole = parts[1];
            var right = new FunctionalRight(parts[2]);
            await _functionalRightRepository.DeleteMappingAsync(tenantId, realmId, groupOrRole, right, ct).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException(
                $"Recertification asset type '{assetType}' is not supported for revocation; the access behind '{assetKey}' was not revoked.");
        }
    }
}
