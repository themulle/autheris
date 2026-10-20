namespace Autheris.Application.Governance.Policies;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;

public sealed record PolicyEvaluationContext(
    ClaimsPrincipal Caller,
    TenantId TenantId,
    string RealmId,
    TableIdentifier TableName,
    IReadOnlySet<string> TableTags,
    bool IsDelegatedSource = false
);

public sealed record ScopedPolicyEvaluationResult(
    AccessPolicyDecision Decision,
    AccessPolicy? MatchedPolicy = null,
    string? Reason = null
)
{
    public AccessPolicy? MatchingPolicy => MatchedPolicy;
    public bool IsPermitted => Decision == AccessPolicyDecision.Permit;
    public bool IsDenied => Decision == AccessPolicyDecision.Deny;

    public static ScopedPolicyEvaluationResult Permit(AccessPolicy policy, string? reason = null) =>
        new(AccessPolicyDecision.Permit, policy, reason ?? $"Permitted by scoped access policy '{policy.Name}'.");

    public static ScopedPolicyEvaluationResult Deny(AccessPolicy? policy = null, string? reason = null) =>
        new(AccessPolicyDecision.Deny, policy, reason ?? "Denied by scoped access policy.");
}

public interface IScopedAccessPolicyEngine
{
    Task<ScopedPolicyEvaluationResult> EvaluateAsync(
        PolicyEvaluationContext context,
        CancellationToken ct = default);

    Task CreatePolicyAsync(AccessPolicy policy, CancellationToken ct = default);
    Task CreatePolicyAsync(AccessPolicy policy, Sid actorSid, CancellationToken ct = default);
    Task UpdatePolicyAsync(AccessPolicy policy, CancellationToken ct = default);
    Task UpdatePolicyAsync(AccessPolicy policy, Sid actorSid, CancellationToken ct = default);
    Task DeletePolicyAsync(TenantId tenantId, string policyId, string deletedBy, CancellationToken ct = default);
    Task DeletePolicyAsync(TenantId tenantId, string policyId, Sid actorSid, CancellationToken ct = default);
}
