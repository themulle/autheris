namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Usage;

public partial class GovernanceRepository : IUsageRepository
{
    public Task AccumulateAsync(UsageGenerationSnapshot snapshot, CancellationToken ct = default) =>
        UsageStore.AccumulateAsync(this, snapshot, ct);

    public Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default) =>
        UsageStore.QueryAsync(this, query, ct);

    public Task<int> PurgeAsync(DateOnly cutoffExclusive, int batchSize = 10_000, CancellationToken ct = default) =>
        UsageStore.PurgeAsync(this, cutoffExclusive, batchSize, ct);

    public Task<int> EraseUserAsync(TenantId tenant, string userKey, CancellationToken ct = default) =>
        UsageStore.EraseUserAsync(this, tenant, userKey, ct);

    public Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(
        TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
        UsageStore.GetTariffsAsync(this, tenant, fromUtc, toUtc, ct);

    public Task<UsageTariffVersion> AddTariffVersionAsync(UsageTariffVersion tariff, CancellationToken ct = default) =>
        UsageStore.AddTariffVersionAsync(this, tariff, ct);

    public Task<UsageGrantRefRecord?> GetGrantRefAsync(TenantId tenant, string grantId, CancellationToken ct = default) =>
        UsageStore.GetGrantRefAsync(this, tenant, grantId, ct);

    public Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(
        TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        UsageStore.QueryConsumerAppDailyAsync(this, tableId, tenantId, fromUtc, toUtc, ct);

    public Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(
        TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        UsageStore.QueryDistinctActorCountsAsync(this, tableId, tenantId, fromUtc, toUtc, ct);

    public Task<UsageQuotaTenantSnapshot> GetQuotaSnapshotAsync(
        TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
        UsageStore.GetQuotaSnapshotAsync(this, tenant, fromUtc, toUtc, ct);

    public Task<IReadOnlyList<TenantId>> GetActiveUsageTenantsAsync(
        DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
        UsageStore.GetActiveUsageTenantsAsync(this, fromUtc, toUtc, ct);
}
