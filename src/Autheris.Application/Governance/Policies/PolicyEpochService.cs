namespace Autheris.Application.Governance.Policies;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public interface IPolicyEpochService
{
    ValueTask<long> GetEpochAsync(TenantId tenantId, CancellationToken ct = default);
    ValueTask<long> IncrementEpochAsync(TenantId tenantId, CancellationToken ct = default);
}

public sealed class PolicyEpochService : IPolicyEpochService
{
    private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<long> GetEpochAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var epoch = _epochs.GetOrAdd(tenantId.Value, 1);
        return ValueTask.FromResult(epoch);
    }

    public ValueTask<long> IncrementEpochAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var next = _epochs.AddOrUpdate(tenantId.Value, 2, (_, current) => current + 1);
        return ValueTask.FromResult(next);
    }
}
