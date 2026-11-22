using Autheris.Domain.Common;

namespace Autheris.Application.Governance.Caching;

/// <summary>Reads and monotonically advances per-tenant governance epoch counters in durable storage.</summary>
public interface IGovernanceEpochStore
{
    Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default);
    Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default);
}
