using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Common.Interfaces;

public enum AccessPolicyWriteStatus { Created = 0, Updated = 1, Conflict = 2 }

public sealed record AccessPolicyWriteResult(
    AccessPolicyWriteStatus Status,
    StoredTenantAccessPolicy? Stored = null)
{
    public bool Succeeded => Status is AccessPolicyWriteStatus.Created or AccessPolicyWriteStatus.Updated;
}

/// <summary>Tenant-isolated persistence for tenant access policy overrides (<c>access_policy_settings</c>).</summary>
public interface IAccessPolicySettingsRepository
{
    Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default);
    Task<AccessPolicyWriteResult> UpsertAsync(TenantId tenant, TenantAccessPolicyUpdate settings, int expectedVersion, Sid actor, CancellationToken ct = default);
}
