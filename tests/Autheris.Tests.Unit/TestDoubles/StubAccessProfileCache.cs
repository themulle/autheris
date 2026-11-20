using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Unit.TestDoubles;

internal sealed class StubAccessProfileCache : IAccessProfileCache
{
    public List<TenantId> InvalidatedTenants { get; } = [];
    public Func<TenantId, CancellationToken, Task>? OnInvalidateTenant { get; set; }

    public ValueTask<IReadOnlyList<AccessProfile>> GetProfilesAsync(TenantId tenantId, string subject, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AccessProfile>>([]);

    public ValueTask<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(
        TenantId tenantId,
        IReadOnlyCollection<string> users,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> roles,
        CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AccessProfile>>([]);

    public Task InvalidateTenantAsync(TenantId tenantId, CancellationToken ct = default)
    {
        InvalidatedTenants.Add(tenantId);
        return OnInvalidateTenant is not null ? OnInvalidateTenant(tenantId, ct) : Task.CompletedTask;
    }
}
