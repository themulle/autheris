namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;

public partial class SqliteGovernanceRepository : IRecertificationRepository
{
    private RecertificationStore? _recertificationStore;

    private RecertificationStore RecertificationStoreInstance => _recertificationStore ??= new RecertificationStore(RecertificationDialect.Sqlite, async (write, ct) =>
    {
        var scope = write
            ? await WriteScopeAsync(ct).ConfigureAwait(false)
            : await ReadScopeAsync(ct).ConfigureAwait(false);
        return new RecertificationScope(scope.Connection, scope.DisposeAsync);
    });

    public Task CreateCampaignAsync(RecertificationCampaign campaign, CancellationToken ct = default) =>
        RecertificationStoreInstance.CreateCampaignAsync(campaign, ct);

    public Task<IReadOnlyList<RecertificationCampaign>> GetCampaignsAsync(TenantId tenantId, CancellationToken ct = default) =>
        RecertificationStoreInstance.GetCampaignsAsync(tenantId, ct);

    public Task<RecertificationCampaign?> GetCampaignByIdAsync(TenantId tenantId, string campaignId, CancellationToken ct = default) =>
        RecertificationStoreInstance.GetCampaignByIdAsync(tenantId, campaignId, ct);

    public Task UpdateCampaignStatusAsync(TenantId tenantId, string campaignId, RecertCampaignStatus status, CancellationToken ct = default) =>
        RecertificationStoreInstance.UpdateCampaignStatusAsync(tenantId, campaignId, status, ct);

    public Task CreateItemAsync(RecertificationItem item, CancellationToken ct = default) =>
        RecertificationStoreInstance.CreateItemAsync(item, ct);

    public Task<IReadOnlyList<RecertificationItem>> GetItemsAsync(TenantId tenantId, string campaignId, CancellationToken ct = default) =>
        RecertificationStoreInstance.GetItemsAsync(tenantId, campaignId, ct);

    public Task<RecertificationItem?> GetItemByIdAsync(TenantId tenantId, string campaignId, string itemId, CancellationToken ct = default) =>
        RecertificationStoreInstance.GetItemByIdAsync(tenantId, campaignId, itemId, ct);

    public Task<IReadOnlyList<RecertificationItem>> GetPendingItemsPastDeadlineAsync(DateTimeOffset deadlineUtc, CancellationToken ct = default) =>
        RecertificationStoreInstance.GetPendingItemsPastDeadlineAsync(deadlineUtc, ct);

    public Task UpdateItemDecisionAsync(TenantId tenantId, string campaignId, string itemId, RecertDecision decision, string decisionBy, string? comments = null, CancellationToken ct = default) =>
        RecertificationStoreInstance.UpdateItemDecisionAsync(tenantId, campaignId, itemId, decision, decisionBy, comments, ct);
}
