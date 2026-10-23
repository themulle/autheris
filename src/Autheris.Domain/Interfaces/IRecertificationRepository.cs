namespace Autheris.Domain.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;

public interface IRecertificationRepository
{
    Task CreateCampaignAsync(RecertificationCampaign campaign, CancellationToken ct = default);
    Task<IReadOnlyList<RecertificationCampaign>> GetCampaignsAsync(TenantId tenantId, CancellationToken ct = default);
    Task<RecertificationCampaign?> GetCampaignByIdAsync(TenantId tenantId, string campaignId, CancellationToken ct = default);
    Task UpdateCampaignStatusAsync(TenantId tenantId, string campaignId, RecertCampaignStatus status, CancellationToken ct = default);

    Task CreateItemAsync(RecertificationItem item, CancellationToken ct = default);
    Task<IReadOnlyList<RecertificationItem>> GetItemsAsync(TenantId tenantId, string campaignId, CancellationToken ct = default);
    Task<RecertificationItem?> GetItemByIdAsync(TenantId tenantId, string campaignId, string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<RecertificationItem>> GetPendingItemsPastDeadlineAsync(DateTimeOffset deadlineUtc, CancellationToken ct = default);
    /// <summary>
    /// Compare-and-set: updates the decision only while the stored decision equals <paramref name="expected"/>.
    /// Returns false (record unchanged) when the item is missing or was decided concurrently.
    /// </summary>
    Task<bool> TryUpdateItemDecisionAsync(TenantId tenantId, string campaignId, string itemId, RecertDecision expected, RecertDecision decision, string decisionBy, string? comments = null, CancellationToken ct = default);
}
