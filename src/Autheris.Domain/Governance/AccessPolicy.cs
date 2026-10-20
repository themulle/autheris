namespace Autheris.Domain.Governance;

using System;
using System.Collections.Generic;

public enum AccessPolicyGranteeType
{
    Group = 1,
    Role = 2,
    User = 3
}

public enum AccessPolicySelectorType
{
    TablePattern = 1,
    CatalogTag = 2
}

public enum AccessPolicyDecision
{
    Permit = 1,
    Deny = 2
}

public sealed record AccessPolicyGrantee(
    string TenantId,
    string PolicyId,
    string RealmId,
    AccessPolicyGranteeType GranteeType,
    string GranteeId
);

public sealed record AccessPolicySelector(
    string TenantId,
    string PolicyId,
    AccessPolicySelectorType SelectorType,
    string SelectorValue
);

public sealed class AccessPolicy
{
    public string TenantId { get; set; } = string.Empty;
    public string PolicyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int Priority { get; set; } = 100;
    public AccessPolicyDecision Decision { get; set; } = AccessPolicyDecision.Permit;
    public string CreatedBy { get; set; } = string.Empty;
    public string? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }

    public List<AccessPolicyGrantee> Grantees { get; set; } = [];
    public List<AccessPolicySelector> Selectors { get; set; } = [];

    public void AddGrantee(string realmId, AccessPolicyGranteeType granteeType, string granteeId)
    {
        if (granteeType != AccessPolicyGranteeType.Group && granteeType != AccessPolicyGranteeType.Role)
        {
            throw new ArgumentException("Access Policies strictly forbid individual User grantees. Use time-bound grants for users.");
        }

        Grantees.Add(new AccessPolicyGrantee(TenantId, PolicyId, realmId, granteeType, granteeId));
    }

    public void AddSelector(AccessPolicySelectorType selectorType, string selectorValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectorValue);
        Selectors.Add(new AccessPolicySelector(TenantId, PolicyId, selectorType, selectorValue));
    }
}
