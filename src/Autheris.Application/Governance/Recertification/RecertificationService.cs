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

public sealed class RecertificationService : IRecertificationService
{
    private readonly IRecertificationRepository _repository;
    private readonly IFunctionalRightRepository? _functionalRightRepository;
    private readonly IAccessPolicyRepository? _accessPolicyRepository;
    private readonly IPolicyEpochService? _epochService;
    private readonly IAuditWriter? _auditWriter;

    public RecertificationService(
        IRecertificationRepository repository,
        IFunctionalRightRepository? functionalRightRepository = null,
        IAccessPolicyRepository? accessPolicyRepository = null,
        IPolicyEpochService? epochService = null,
        IAuditWriter? auditWriter = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _functionalRightRepository = functionalRightRepository;
        _accessPolicyRepository = accessPolicyRepository;
        _epochService = epochService;
        _auditWriter = auditWriter;
    }

    public async Task<RecertificationCampaign> CreateCampaignAsync(
        TenantId tenantId,
        string name,
        RecertClass recertClass,
        DateTimeOffset? deadline = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

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

        await _repository.UpdateItemDecisionAsync(
            tenantId, campaignId, itemId, RecertDecision.Revoked, reviewerId, comments, ct).ConfigureAwait(false);

        await DeactivateUnderlyingAssetAsync(new TenantId(item.TenantId), item.AssetType, item.AssetKey, ct).ConfigureAwait(false);
    }

    public async Task<int> ExecuteAutoRevocationCycleAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        var expiredPending = await _repository.GetPendingItemsPastDeadlineAsync(asOf, ct).ConfigureAwait(false);
        int revokedCount = 0;

        foreach (var item in expiredPending)
        {
            var tenantId = new TenantId(item.TenantId);

            // 1. Mark as Revoked by SYSTEM_AUTO_REVOKE
            await _repository.UpdateItemDecisionAsync(
                tenantId, item.CampaignId, item.ItemId, RecertDecision.Revoked, "SYSTEM_AUTO_REVOKE",
                "Automated revocation fail-closed upon 14-day deadline expiry", ct).ConfigureAwait(false);

            // 2. Deactivate the underlying permission
            await DeactivateUnderlyingAssetAsync(tenantId, item.AssetType, item.AssetKey, ct).ConfigureAwait(false);

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

            revokedCount++;
        }

        return revokedCount;
    }

    private async Task DeactivateUnderlyingAssetAsync(TenantId tenantId, string assetType, string assetKey, CancellationToken ct)
    {
        if (string.Equals(assetType, "FunctionalRightMapping", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assetType, "FUNCTIONAL_RIGHT_MAPPINGS", StringComparison.OrdinalIgnoreCase))
        {
            // assetKey format: "realmId:groupOrRole:functionalRight"
            var parts = assetKey.Split(':');
            if (parts.Length == 3 && _functionalRightRepository != null)
            {
                var realmId = parts[0];
                var groupOrRole = parts[1];
                var right = new FunctionalRight(parts[2]);
                await _functionalRightRepository.DeleteMappingAsync(tenantId, realmId, groupOrRole, right, ct).ConfigureAwait(false);
            }
        }
        else if (string.Equals(assetType, "AccessPolicy", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(assetType, "ACCESS_POLICIES", StringComparison.OrdinalIgnoreCase))
        {
            if (_accessPolicyRepository != null)
            {
                var policy = await _accessPolicyRepository.GetPolicyByIdAsync(tenantId, assetKey, ct).ConfigureAwait(false);
                if (policy != null)
                {
                    policy.IsActive = false;
                    await _accessPolicyRepository.UpdatePolicyAsync(policy, ct).ConfigureAwait(false);
                }
            }

            if (_epochService != null)
            {
                await _epochService.IncrementEpochAsync(tenantId, ct).ConfigureAwait(false);
            }
        }
    }
}
