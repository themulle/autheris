using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Services;

public sealed class ConsentResolutionService : IConsentResolutionService
{
    private readonly IRowFilterSqlBuilder _sqlBuilder;

    public ConsentResolutionService(IRowFilterSqlBuilder? sqlBuilder = null)
    {
        _sqlBuilder = sqlBuilder ?? new RowFilterSqlBuilder();
    }

    public TableAccessDecision ResolveAccess(
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles,
        TableIdentifier table,
        IReadOnlyList<Consent> activeConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Identify applicable active consents for Subject Set S
        var applicable = activeConsents
            .Where(c => c.IsActive(now) && c.TableIdentifier == table && IsSubjectMatch(c, userSid, subjectGroupSids, userRoles))
            .ToList();

        // Separate into A (ALLOW) and D (DENY)
        var aConsents = applicable.Where(c => c.Effect == ConsentEffect.Allow).ToList();
        var dConsents = applicable.Where(c => c.Effect == ConsentEffect.Deny).ToList();

        // Rule 1: Hard Table DENY
        // A deny consent with no column rules is considered a table-level DENY
        var hasTableDeny = dConsents.Any(c => c.ColumnRules.Count == 0 && c.RowFilters.Count == 0);
        if (hasTableDeny)
        {
            return TableAccessDecision.Denied(table, "Hard DENY: Active table-level DENY applies to subject.");
        }

        // Rule 2: Zero Trust - If A is empty, deny access
        if (aConsents.Count == 0)
        {
            return TableAccessDecision.Denied(table, "Zero Trust: No active ALLOW consent granted to subject.");
        }

        // Check for Column Hard-DENYs in D
        var hardDeniedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maskedOnlyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dConsents)
        {
            foreach (var colRule in c.ColumnRules)
            {
                if (colRule.AccessLevel == ColumnAccessLevel.Deny)
                {
                    hardDeniedColumns.Add(colRule.ColumnName);
                }
                else if (colRule.AccessLevel == ColumnAccessLevel.Mask)
                {
                    maskedOnlyColumns.Add(colRule.ColumnName);
                }
            }
        }

        // Rule 3: Column Access Levels (Bounded by row-filter scope to prevent consent-blending privilege escalation)
        // SEC H-11: The effective level of a column is computed over ALL allow consents, not only over the consents that
        // mention the column. A consent without a rule for a column contributes "Deny" for that column (unless it has no
        // column rules at all, which means "all columns Clear"). Allow consents are grouped by their row filter:
        //   - rows of the same filter group are visible through every consent of that group -> maximum within the group
        //   - rows of different filter groups are only visible through their own group -> minimum across groups
        //   - if an unconstrained (no row filter) consent exists, rows outside every filter are visible through the
        //     unconstrained consents only -> their maximum is the bound for the whole result
        // This prevents a column released by consent B (rows US) from becoming Clear for the rows of consent A (rows EU).
        var allReferencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in applicable)
        {
            foreach (var cr in c.ColumnRules)
            {
                allReferencedColumns.Add(cr.ColumnName);
            }
        }

        var unconstrainedConsents = aConsents.Where(c => c.RowFilters.Count == 0).ToList();
        var rowConstrainedGroups = unconstrainedConsents.Count > 0
            ? new List<List<Consent>>()
            : BuildRowFilterGroups(aConsents, dialect);

        var columnAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in allReferencedColumns)
        {
            if (hardDeniedColumns.Contains(column))
            {
                columnAccess[column] = ColumnAccessLevel.Deny;
                continue;
            }

            var level = ResolveColumnLevel(column, unconstrainedConsents, rowConstrainedGroups);

            if (maskedOnlyColumns.Contains(column) && level == ColumnAccessLevel.Clear)
            {
                level = ColumnAccessLevel.Mask;
            }

            columnAccess[column] = level;
        }

        // Rule 4: Row Predicate resolution delegated to dedicated IRowFilterSqlBuilder
        string? rowFilterSql = _sqlBuilder.BuildCombinedRowFilter(aConsents, dConsents, dialect);

        // SEC H-11: Columns without any explicit rule (e.g. 'ssn' when only 'name' is mentioned) default to Clear only if
        // the same union semantics yield Clear for an unmentioned column, i.e. every row of the result is visible through
        // at least one allow consent without column rules. Previously a single row-constrained consent without column
        // rules released every unmentioned column for the rows of all other consents.
        bool hasUnconstrainedColumnAllow =
            ResolveColumnLevel(column: null, unconstrainedConsents, rowConstrainedGroups) == ColumnAccessLevel.Clear;

        return TableAccessDecision.Allowed(table, columnAccess, rowFilterSql, hasUnconstrainedColumnAllow);
    }

    /// <summary>
    /// SEC (Low): Cache lifetime of a consent decision. Bounded by the earliest <c>ValidTo</c> of the consents the decision
    /// was derived from, so an expired consent is never served from the cache.
    /// </summary>
    public static TimeSpan ComputeDecisionCacheTtl(bool isHighlySensitive, IReadOnlyList<Consent> consents, DateTimeOffset now)
    {
        var ttl = isHighlySensitive
            ? TimeSpan.FromSeconds(60)
            : TimeSpan.FromMinutes(10);

        if (consents == null || consents.Count == 0)
        {
            return ttl;
        }

        var earliestExpiry = consents
            .Where(c => c.ValidTo > now && c.ValidFrom <= now)
            .Select(c => c.ValidTo - now)
            .DefaultIfEmpty(ttl)
            .Min();

        // POL-8: If there is an upcoming scheduled DENY consent (ValidFrom > now),
        // the cache TTL of an ALLOW decision must be bounded by ValidFrom - now so that the scheduled DENY takes effect on time!
        var earliestUpcomingDeny = consents
            .Where(c => c.Effect == ConsentEffect.Deny && !c.IsRevoked && c.ValidFrom > now)
            .Select(c => c.ValidFrom - now)
            .DefaultIfEmpty(ttl)
            .Min();

        var minWindow = earliestExpiry < earliestUpcomingDeny ? earliestExpiry : earliestUpcomingDeny;

        if (minWindow < ttl)
        {
            ttl = minWindow > TimeSpan.FromSeconds(1) ? minWindow : TimeSpan.FromSeconds(1);
        }

        return ttl;
    }

    private List<List<Consent>> BuildRowFilterGroups(IReadOnlyList<Consent> allowConsents, DatabaseDialect dialect)
    {
        var groups = new Dictionary<string, List<Consent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var consent in allowConsents)
        {
            var key = _sqlBuilder.BuildCombinedRowFilter(new[] { consent }, Array.Empty<Consent>(), dialect) ?? string.Empty;
            if (!groups.TryGetValue(key, out var members))
            {
                members = new List<Consent>();
                groups[key] = members;
            }
            members.Add(consent);
        }

        return groups.Values.ToList();
    }

    private static ColumnAccessLevel ResolveColumnLevel(
        string? column,
        IReadOnlyList<Consent> unconstrainedConsents,
        IReadOnlyList<List<Consent>> rowConstrainedGroups)
    {
        if (unconstrainedConsents.Count > 0)
        {
            // Rows outside every row filter are visible only through the unconstrained consents.
            var maxLevel = unconstrainedConsents.Max(c => GetConsentColumnLevel(c, column));
            return Enum.IsDefined(maxLevel) ? maxLevel : ColumnAccessLevel.Deny;
        }

        if (rowConstrainedGroups.Count == 0)
        {
            return ColumnAccessLevel.Deny;
        }

        var result = ColumnAccessLevel.Clear;
        foreach (var group in rowConstrainedGroups)
        {
            var rawGroupLevel = group.Max(c => GetConsentColumnLevel(c, column));
            var groupLevel = Enum.IsDefined(rawGroupLevel) ? rawGroupLevel : ColumnAccessLevel.Deny;
            if (groupLevel < result)
            {
                result = groupLevel;
            }
        }

        return result;
    }

    private static ColumnAccessLevel GetConsentColumnLevel(Consent consent, string? column)
    {
        if (consent.ColumnRules.Count == 0)
        {
            return ColumnAccessLevel.Clear;
        }

        if (column == null)
        {
            return ColumnAccessLevel.Deny;
        }

        var rule = consent.ColumnRules.FirstOrDefault(cr => string.Equals(cr.ColumnName, column, StringComparison.OrdinalIgnoreCase));
        if (rule == null)
        {
            return ColumnAccessLevel.Deny;
        }

        return Enum.IsDefined(rule.AccessLevel)
            ? rule.AccessLevel
            : ColumnAccessLevel.Deny;
    }

    private static bool IsSubjectMatch(
        Consent consent,
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles)
    {
        switch (consent.GranteeType)
        {
            case GranteeType.User:
            case GranteeType.ServicePrincipal:
                return consent.GranteeSid.HasValue && consent.GranteeSid.Value == userSid;

            case GranteeType.Group:
                return consent.GranteeSid.HasValue && subjectGroupSids.Contains(consent.GranteeSid.Value);

            case GranteeType.Role:
                if (!string.IsNullOrEmpty(consent.RoleName) && userRoles.Contains(consent.RoleName))
                {
                    return true;
                }
                if (consent.RoleId.HasValue && userRoles.Contains(consent.RoleId.Value.ToString()))
                {
                    return true;
                }
                return false;

            default:
                return false;
        }
    }
}
