namespace Autheris.Application.Governance.Recertification;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;

public interface IRecertificationService
{
    Task<RecertificationCampaign> CreateCampaignAsync(
        TenantId tenantId,
        string name,
        RecertClass recertClass,
        DateTimeOffset? deadline = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<RecertificationCampaign>> GetCampaignsAsync(TenantId tenantId, CancellationToken ct = default);
    Task<RecertificationCampaign?> GetCampaignByIdAsync(TenantId tenantId, string campaignId, CancellationToken ct = default);

    Task<RecertificationItem> AddItemAsync(
        TenantId tenantId,
        string campaignId,
        string assetType,
        string assetKey,
        string assignedTo,
        string reviewerId,
        CancellationToken ct = default);

    Task<IReadOnlyList<RecertificationItem>> GetItemsAsync(TenantId tenantId, string campaignId, CancellationToken ct = default);

    Task CertifyItemAsync(
        TenantId tenantId,
        string campaignId,
        string itemId,
        string reviewerId,
        string? comments = null,
        CancellationToken ct = default);

    Task RevokeItemAsync(
        TenantId tenantId,
        string campaignId,
        string itemId,
        string reviewerId,
        string? comments = null,
        CancellationToken ct = default);

    Task<int> ExecuteAutoRevocationCycleAsync(DateTimeOffset asOf, CancellationToken ct = default);
}
