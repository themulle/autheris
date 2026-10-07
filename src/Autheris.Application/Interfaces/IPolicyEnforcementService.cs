namespace Autheris.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public interface IPolicyEnforcementService
{
    /// <summary>
    /// Evaluates Casbin ABAC policy rules against the security evaluation context.
    /// Hot-path: Returns ValueTask for zero heap allocations on in-memory hits.
    /// Execution SLA: p99 <= 0.5 ms, p50 <= 0.1 ms with 50,000 active rules.
    /// </summary>
    ValueTask<TableAccessDecision> EvaluatePolicyAsync(
        SecurityEvaluationContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Checks whether active Casbin ABAC policies are registered for the given tenant.
    /// </summary>
    bool HasPolicies(TenantId tenant);

    /// <summary>
    /// Synchronizes updated policies from Redis event bus invalidations (<= 50 ms).
    /// </summary>
    Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default);

    /// <summary>
    /// Loads or reloads policies for the specified tenant from CSV or policy definition text.
    /// </summary>
    void LoadPolicyFromText(TenantId tenant, string policyText) =>
        throw new NotSupportedException("Policy loading is not supported by this policy enforcement implementation.");

    /// <summary>
    /// Loads or reloads policies from CSV or policy definition text across all defined tenants or wildcards.
    /// </summary>
    void LoadPolicyFromText(string policyText) =>
        throw new NotSupportedException("Policy loading is not supported by this policy enforcement implementation.");

    /// <summary>
    /// Loads policies from a file and optionally watches the file for changes to hot-reload without restarting the host.
    /// </summary>
    void LoadPolicyFromFile(TenantId tenant, string filePath, bool watchFile = false) =>
        throw new NotSupportedException("Policy loading is not supported by this policy enforcement implementation.");

    /// <summary>
    /// Loads policies from a file across all defined tenants and optionally watches the file for changes to hot-reload.
    /// </summary>
    void LoadPolicyFromFile(string filePath, bool watchFile = false) =>
        throw new NotSupportedException("Policy loading is not supported by this policy enforcement implementation.");
}

