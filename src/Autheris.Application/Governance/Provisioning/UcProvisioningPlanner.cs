namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>One privilege the active consents want, with the resolved principal and every consent that wants it.</summary>
public sealed record UcDesiredPrivilege(UcPrivilegeKey Key, string Principal, IReadOnlyList<Guid> ConsentIds);

/// <summary>
/// The planned outcome of one active consent: its privilege keys, the reason it is not provisioned, or (<see cref="PendingReason"/>)
/// why it could not be planned in this cycle.
/// </summary>
public sealed record UcConsentPlan(
    Consent Consent, IReadOnlyList<UcPrivilegeKey> Keys, string? UnsupportedReason, string? Catalog, string? Principal, string? PendingReason = null)
{
    public bool IsProvisionable => UnsupportedReason is null && PendingReason is null;
}

/// <summary>
/// The desired state of one data source: the wanted privileges, the plan of every active consent, and the principals whose
/// consents could not be planned this cycle (their ledger privileges are neither revoked nor counted as unwanted).
/// </summary>
public sealed record UcDesiredState(
    IReadOnlyDictionary<UcPrivilegeKey, UcDesiredPrivilege> Privileges,
    IReadOnlyList<UcConsentPlan> Consents,
    IReadOnlySet<string>? Protected = null)
{
    public IReadOnlySet<string> ProtectedExternalIds { get; } = Protected ?? new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Pure planning steps of the reconciler. The desired state is derived from the active consents only (level triggered): approval,
/// expiry, renewal, revocation and deprovisioning need no hook, and a renewal that keeps the same privileges changes nothing.
/// </summary>
public static class UcProvisioningPlanner
{
    /// <summary>The identity provider id form used in the ledger: a GUID in lower-case "D" format, or null for anything else.</summary>
    public static string? NormalizeExternalId(string? id) => Guid.TryParse(id?.Trim(), out var guid) ? guid.ToString("D") : null;

    /// <summary>Whether an active deny consent of the tenant covers the table: a table deny or a pattern deny that matches it.</summary>
    public static bool HasDeny(TableIdentifier table, IEnumerable<Consent> tableConsents, IEnumerable<Consent> patternConsents)
    {
        foreach (var consent in tableConsents)
        {
            if (consent.Effect != ConsentEffect.Allow && consent.TableIdentifier.Equals(table))
            {
                return true;
            }
        }

        foreach (var consent in patternConsents)
        {
            if (consent.Effect == ConsentEffect.Allow)
            {
                continue;
            }

            // A pattern that cannot be parsed is treated as covering the table (fail closed).
            if (!ObjectPatternSpec.TryParse(consent.ObjectPattern, out var spec, out _) || spec!.Matches(table))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the desired privileges from the eligibility results and the principal resolutions. A provisionable consent wants
    /// <c>SELECT</c> on its table and <c>USE_SCHEMA</c> on the table's schema for its resolved principal; a consent whose grantee is
    /// unresolved or ambiguous wants nothing.
    /// </summary>
    public static UcDesiredState BuildDesired(
        IReadOnlyList<(Consent Consent, UcEligibility Eligibility)> evaluated,
        IReadOnlyDictionary<(GranteeType Type, string ExternalId), UcPrincipalResolution> resolutions,
        IReadOnlyDictionary<Guid, string>? uncertain = null)
    {
        ArgumentNullException.ThrowIfNull(evaluated);
        ArgumentNullException.ThrowIfNull(resolutions);
        var wanted = new Dictionary<UcPrivilegeKey, (string Principal, List<Guid> Consents)>();
        var plans = new List<UcConsentPlan>(evaluated.Count);
        var protectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (consent, eligibility) in evaluated)
        {
            if (uncertain is not null && uncertain.TryGetValue(consent.Id, out var pending))
            {
                // Not planned this cycle: nothing is granted for it, and nothing its principal holds is revoked.
                plans.Add(new UcConsentPlan(consent, [], null, null, null, pending));
                if (NormalizeExternalId(consent.GranteeSid?.Value) is { } held)
                {
                    protectedIds.Add(held);
                }

                continue;
            }

            if (eligibility.Target is not { } target)
            {
                plans.Add(new UcConsentPlan(consent, [], eligibility.ReasonCode ?? UcReasonCodes.DataSourceNotProvisioned, null, null));
                continue;
            }

            var externalId = NormalizeExternalId(consent.GranteeSid?.Value);
            UcPrincipalResolution? resolution = null;
            if (externalId is not null)
            {
                resolutions.TryGetValue((consent.GranteeType, externalId), out resolution);
            }

            if (resolution is not { Status: UcPrincipalResolutionStatus.Resolved, Principal: not null })
            {
                var reason = resolution?.Status == UcPrincipalResolutionStatus.Ambiguous ? UcReasonCodes.PrincipalAmbiguous : UcReasonCodes.PrincipalUnresolved;
                plans.Add(new UcConsentPlan(consent, [], reason, null, null));
                continue;
            }

            var principal = resolution.Principal;
            UcPrivilegeKey[] keys =
            [
                new(UcSecurableType.Schema, target.SchemaFullName, principal.ExternalId, UcPrivileges.UseSchema),
                new(UcSecurableType.Table, target.FullName, principal.ExternalId, target.Privilege)
            ];
            foreach (var key in keys)
            {
                if (!wanted.TryGetValue(key, out var entry))
                {
                    entry = (principal.Name, []);
                    wanted[key] = entry;
                }

                entry.Consents.Add(consent.Id);
            }

            plans.Add(new UcConsentPlan(consent, keys, null, target.Catalog, principal.Name));
        }

        var desired = wanted.ToDictionary(
            kv => kv.Key,
            kv => new UcDesiredPrivilege(kv.Key, kv.Value.Principal, kv.Value.Consents.Distinct().OrderBy(id => id).ToList()));
        return new UcDesiredState(desired, plans, protectedIds);
    }

    /// <summary>Exponential backoff from 30 seconds, capped at <paramref name="cap"/>.</summary>
    public static TimeSpan Backoff(int attempts, TimeSpan cap)
    {
        var seconds = 30d * Math.Pow(2, Math.Clamp(attempts - 1, 0, 16));
        var delay = TimeSpan.FromSeconds(seconds);
        return delay < cap ? delay : cap;
    }

    /// <summary>The tenant a provisioning section names, or null when it is missing or invalid (nothing is provisioned then).</summary>
    public static TenantId? ProvisioningTenant(DatabricksProvisioningOptions? provisioning) =>
        provisioning?.Tenant is { } raw && TenantId.TryParse(raw.Trim(), out var tenant) ? (TenantId?)tenant : null;
}
