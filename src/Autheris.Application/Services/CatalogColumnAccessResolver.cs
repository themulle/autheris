namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Issue #127: column levels for catalog-side outputs (value profiles, synthetic samples, query analysis). Every level comes
/// from <see cref="IConsentResolutionService"/> and <see cref="TableAccessDecision.GetEffectiveColumnAccess"/>, i.e. the same
/// resolution the data path uses; no consent column rule is interpreted here. Callers pass the scopes exactly as the data path
/// does (<c>TokenAccessScope.GetScopeSet(principal.Claims)</c>, <c>null</c> when empty). Methods throw on resolver failures;
/// callers translate that into <see cref="ColumnAccessLevel.Deny"/> for every column.
/// </summary>
internal static class CatalogColumnAccessResolver
{
    /// <summary>Scopes as the data path hands them to the resolver: an empty set is passed as <c>null</c>.</summary>
    public static IReadOnlySet<string>? ForResolver(IReadOnlySet<string>? scopes) =>
        scopes is { Count: > 0 } ? scopes : null;

    /// <summary>Every column of the table at <see cref="ColumnAccessLevel.Deny"/> (fail-closed answer).</summary>
    public static Dictionary<string, ColumnAccessLevel> AllDeny(TableMetadata table)
    {
        var levels = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns)
        {
            levels[column.ColumnName] = ColumnAccessLevel.Deny;
        }

        return levels;
    }

    /// <summary>
    /// Level per column for a caller whose consents are evaluated by the data-path resolver. A denied or missing decision
    /// (no applicable Allow consent, hard Deny, subject or time mismatch) yields <see cref="ColumnAccessLevel.Deny"/> for every column.
    /// <paramref name="tenantConsents"/> must already be restricted to the caller's tenant.
    /// </summary>
    public static Dictionary<string, ColumnAccessLevel> ResolveGranted(
        IConsentResolutionService resolver,
        TableMetadata table,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<string> roles,
        IReadOnlyList<Consent> tenantConsents,
        IReadOnlySet<string>? callerScopes)
    {
        var decision = resolver.ResolveAccess(
            userSid, groupSids, roles, table.Identifier, tenantConsents, table.Dialect, allUserSids: null, ForResolver(callerScopes));

        return decision is { IsAllowed: true }
            ? LevelsFromDecision(table, decision)
            : AllDeny(table);
    }

    /// <summary>
    /// Level per column for a caller without an Allow consent on the table (feature <c>ReleaseProfileToCatalogViewers</c>):
    /// the catalog baseline (Clear, Mask for catalog-sensitive columns) lowered by every Deny-effect consent. The baseline is
    /// obtained by resolving the Deny-effect consents together with a synthetic unconditional Allow for the caller, so
    /// hard table Deny, column Deny and Deny-effect Mask follow the data-path rules (scope-bound rules apply to scope holders only).
    /// </summary>
    public static Dictionary<string, ColumnAccessLevel> ResolveBaseline(
        IConsentResolutionService resolver,
        TableMetadata table,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<string> roles,
        IReadOnlyList<Consent> tenantConsents,
        IReadOnlySet<string>? callerScopes)
    {
        var now = DateTimeOffset.UtcNow;
        var consents = tenantConsents
            .Where(c => c.Effect == ConsentEffect.Deny && c.TableIdentifier == table.Identifier)
            .ToList();

        consents.Add(new Consent
        {
            TableIdentifier = table.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = now.AddMinutes(-1),
            ValidTo = now.AddMinutes(5)
        });

        return ResolveGranted(resolver, table, userSid, groupSids, roles, consents, callerScopes);
    }

    /// <summary>Catalog masking rules of type <c>DENY</c> deny the column for every caller (additive layer, only lowers).</summary>
    public static void ApplyCatalogDenyRules(Dictionary<string, ColumnAccessLevel> levels, TableMetadata table)
    {
        if (table.ColumnMaskingRules == null)
        {
            return;
        }

        foreach (var column in table.Columns)
        {
            if (table.ColumnMaskingRules.TryGetValue(column.ColumnName, out var rule) &&
                string.Equals(rule.RuleType, "DENY", StringComparison.OrdinalIgnoreCase))
            {
                levels[column.ColumnName] = ColumnAccessLevel.Deny;
            }
        }
    }

    /// <summary>
    /// Every scope named by a consent column rule of the table: the worst-case hardening set for a principal whose token
    /// scopes are unknown (impersonated analysis target).
    /// </summary>
    public static IReadOnlySet<string> AllRuleScopes(TableMetadata table, IEnumerable<Consent> consents)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var consent in consents.Where(c => c.TableIdentifier == table.Identifier))
        {
            foreach (var rule in consent.ColumnRules)
            {
                foreach (var scope in rule.ApplicableScopes ?? [])
                {
                    scopes.Add(scope);
                }
            }
        }

        return scopes;
    }

    private static Dictionary<string, ColumnAccessLevel> LevelsFromDecision(TableMetadata table, TableAccessDecision decision)
    {
        var levels = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns)
        {
            var level = decision.GetEffectiveColumnAccess(column.ColumnName, table);
            levels[column.ColumnName] = Enum.IsDefined(level) ? level : ColumnAccessLevel.Deny;
        }

        return levels;
    }
}
