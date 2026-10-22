namespace Autheris.Application.Governance.Policies;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed class ScopedAccessPolicyEngine : IScopedAccessPolicyEngine
{
    private readonly IAccessPolicyRepository _repository;
    private readonly IPolicyEpochService _epochService;
    private readonly IAuditWriter? _auditWriter;

    public ScopedAccessPolicyEngine(
        IAccessPolicyRepository repository,
        IPolicyEpochService epochService,
        IAuditWriter? auditWriter = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _epochService = epochService ?? throw new ArgumentNullException(nameof(epochService));
        _auditWriter = auditWriter;
    }

    public async Task<ScopedPolicyEvaluationResult> EvaluateAsync(
        PolicyEvaluationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in context.Caller.GetGroupSids())
        {
            groups.Add(sid.Value);
            // Also add without realm prefix if namespaced
            var pipeIdx = sid.Value.IndexOf('|');
            if (pipeIdx > 0)
            {
                groups.Add(sid.Value[(pipeIdx + 1)..]);
            }
        }

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in context.Caller.GetUserRoles())
        {
            roles.Add(role);
        }

        var policies = await _repository.GetActivePoliciesForGranteesAsync(
            context.TenantId, context.RealmId, groups, roles, ct).ConfigureAwait(false);

        if (policies.Count == 0)
        {
            return ScopedPolicyEvaluationResult.Deny(null, "No matching access policy found for caller grantees.");
        }

        var matchedPolicies = new List<AccessPolicy>();

        foreach (var policy in policies)
        {
            if (!policy.IsActive) continue;

            bool matchesSelectors = false;
            if (policy.Selectors.Count == 0)
            {
                // No selectors = matches all tables
                matchesSelectors = true;
            }
            else
            {
                foreach (var selector in policy.Selectors)
                {
                    if (selector.SelectorType == AccessPolicySelectorType.TablePattern)
                    {
                        if (PatternMatcher.IsMatch(selector.SelectorValue, context.TableName.ToString()) ||
                            PatternMatcher.IsMatch(selector.SelectorValue, context.TableName.ToQualifiedName()))
                        {
                            matchesSelectors = true;
                            break;
                        }
                    }
                    else if (selector.SelectorType == AccessPolicySelectorType.CatalogTag)
                    {
                        if (context.TableTags != null && context.TableTags.Any(t => string.Equals(t, selector.SelectorValue, StringComparison.OrdinalIgnoreCase)))
                        {
                            matchesSelectors = true;
                            break;
                        }
                    }
                }
            }

            if (matchesSelectors)
            {
                matchedPolicies.Add(policy);
            }
        }

        if (matchedPolicies.Count == 0)
        {
            return ScopedPolicyEvaluationResult.Deny(null, $"No matching policy selector for table '{context.TableName}'.");
        }

        // Evaluate by Priority (descending); on tie, Deny wins (fail-closed)
        var ordered = matchedPolicies
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Decision == AccessPolicyDecision.Deny ? 0 : 1)
            .ToList();

        var winning = ordered[0];
        if (winning.Decision == AccessPolicyDecision.Deny)
        {
            return ScopedPolicyEvaluationResult.Deny(winning, $"Denied by scoped access policy '{winning.Name}'.");
        }

        return ScopedPolicyEvaluationResult.Permit(winning, $"Permitted by scoped access policy '{winning.Name}'.");
    }

    public async Task CreatePolicyAsync(AccessPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // SEC-SC-13: Prohibition of User Grantees
        foreach (var g in policy.Grantees)
        {
            if (g.GranteeType != AccessPolicyGranteeType.Group && g.GranteeType != AccessPolicyGranteeType.Role)
            {
                throw new ArgumentException("Access Policies strictly forbid individual User grantees. Use time-bound grants for users (HTTP 400).");
            }
        }

        await _repository.CreatePolicyAsync(policy, ct).ConfigureAwait(false);

        // Bump epoch
        await _epochService.IncrementEpochAsync(new TenantId(policy.TenantId), ct).ConfigureAwait(false);

        if (_auditWriter != null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = new TenantId(policy.TenantId),
                EventType = "SECURITY_POLICY_MUTATED",
                ActorSid = new Sid(policy.CreatedBy),
                TargetTable = "ACCESS_POLICIES",
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    action = "CREATE",
                    policy_id = policy.PolicyId,
                    name = policy.Name,
                    decision = policy.Decision.ToString(),
                    priority = policy.Priority,
                    grantees_count = policy.Grantees.Count,
                    selectors_count = policy.Selectors.Count
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
    }

    public Task UpdatePolicyAsync(AccessPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return UpdatePolicyCoreAsync(policy, actorSid: null, ct);
    }

    /// <summary>
    /// SEC-SC-14: an update changes the policy content, so a previous approval no longer covers it. Approval fields are reset
    /// (the policy is unapproved / pending re-approval) and the acting user is never recorded as approver of their own change;
    /// the actor is only written to the audit entry. Same rule as <c>AccessPolicyEndpoints.BuildServerSidePolicy</c>, which never
    /// accepts approval fields from input.
    /// </summary>
    private async Task UpdatePolicyCoreAsync(AccessPolicy policy, Sid? actorSid, CancellationToken ct)
    {
        foreach (var g in policy.Grantees)
        {
            if (g.GranteeType != AccessPolicyGranteeType.Group && g.GranteeType != AccessPolicyGranteeType.Role)
            {
                throw new ArgumentException("Access Policies strictly forbid individual User grantees. Use time-bound grants for users (HTTP 400).");
            }
        }

        policy.ApprovedBy = null;
        policy.ApprovedAt = null;

        await _repository.UpdatePolicyAsync(policy, ct).ConfigureAwait(false);

        await _epochService.IncrementEpochAsync(new TenantId(policy.TenantId), ct).ConfigureAwait(false);

        if (_auditWriter != null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = new TenantId(policy.TenantId),
                EventType = "SECURITY_POLICY_MUTATED",
                ActorSid = actorSid ?? new Sid(policy.CreatedBy),
                TargetTable = "ACCESS_POLICIES",
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    action = "UPDATE",
                    policy_id = policy.PolicyId,
                    name = policy.Name,
                    approval_reset = true
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
    }

    public async Task DeletePolicyAsync(TenantId tenantId, string policyId, string deletedBy, CancellationToken ct = default)
    {
        await _repository.DeletePolicyAsync(tenantId, policyId, ct).ConfigureAwait(false);

        await _epochService.IncrementEpochAsync(tenantId, ct).ConfigureAwait(false);

        if (_auditWriter != null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "SECURITY_POLICY_MUTATED",
                ActorSid = new Sid(deletedBy),
                TargetTable = "ACCESS_POLICIES",
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    action = "DELETE",
                    policy_id = policyId
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
    }

    public Task CreatePolicyAsync(AccessPolicy policy, Sid actorSid, CancellationToken ct = default)
    {
        policy.CreatedBy = actorSid.Value;
        return CreatePolicyAsync(policy, ct);
    }

    public Task UpdatePolicyAsync(AccessPolicy policy, Sid actorSid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // SEC-SC-14: the actor is the modifier, not an approver (previously ApprovedBy = actor, i.e. a self-approval).
        return UpdatePolicyCoreAsync(policy, actorSid, ct);
    }

    public Task DeletePolicyAsync(TenantId tenantId, string policyId, Sid actorSid, CancellationToken ct = default) =>
        DeletePolicyAsync(tenantId, policyId, actorSid.Value, ct);
}
