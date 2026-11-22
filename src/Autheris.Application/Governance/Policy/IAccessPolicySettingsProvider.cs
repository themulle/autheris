using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Governance.Policy;

public enum AccessPolicyUpdateStatus { Updated = 0, Conflict = 1, Invalid = 2 }

public sealed record AccessPolicyUpdateResult(
    AccessPolicyUpdateStatus Status,
    TenantAccessPolicy? Policy = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>Resolves deployment-clamped tenant access policies and applies audited tenant updates.</summary>
public interface IAccessPolicySettingsProvider
{
    Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default);
    Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default);
}
