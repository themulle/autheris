using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

namespace Autheris.Tests.Unit.TestDoubles;

internal sealed class StubTenantRegistry : ITenantRegistry
{
    public List<(TenantId Tenant, string? Issuer)> RegisterCalls { get; } = [];
    public List<TenantId> TryGetCalls { get; } = [];
    public List<(TenantId Keep, string? ExpectedCurrent)> ResolveCalls { get; } = [];
    public List<IReadOnlyList<string>> PreseedCalls { get; } = [];

    public Func<TenantId, string?, CancellationToken, ValueTask<TenantRegistration>>? OnRegisterOrGet { get; set; }
    public Func<TenantId, CancellationToken, ValueTask<TenantRegistration?>>? OnTryGet { get; set; }
    public Func<TenantId, string?, CancellationToken, ValueTask<TenantCollisionResolution>>? OnResolveCollision { get; set; }

    public ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct)
    {
        RegisterCalls.Add((tenant, issuer));
        return OnRegisterOrGet is not null
            ? OnRegisterOrGet(tenant, issuer, ct)
            : ValueTask.FromResult(TenantRegistration.Registered);
    }

    public ValueTask<TenantRegistration?> TryGetAsync(TenantId tenant, CancellationToken ct)
    {
        TryGetCalls.Add(tenant);
        return OnTryGet is not null
            ? OnTryGet(tenant, ct)
            : ValueTask.FromResult<TenantRegistration?>(null);
    }

    public ValueTask<TenantCollisionResolution> ResolveCollisionAsync(TenantId keep, string? expectedCurrent, CancellationToken ct)
    {
        ResolveCalls.Add((keep, expectedCurrent));
        return OnResolveCollision is not null
            ? OnResolveCollision(keep, expectedCurrent, ct)
            : ValueTask.FromResult(new TenantCollisionResolution(TenantCollisionResolutionStatus.NotFound, null));
    }

    public ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct)
    {
        PreseedCalls.Add(configuredTenantIds.ToArray());
        return ValueTask.CompletedTask;
    }
}
