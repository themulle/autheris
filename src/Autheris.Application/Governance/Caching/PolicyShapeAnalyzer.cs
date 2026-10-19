namespace Autheris.Application.Governance.Caching;

using System;
using System.Text.RegularExpressions;
using Autheris.Application.Caching;

/// <summary>
/// WP-PF-09 / SEC-SC-09: decides, from the text of a rule, whether its outcome can depend on the user, the user's aliases or groups,
/// the client IP or the clock. Only a rule whose conditions and row filter read nothing but the tenant, the object, the action and
/// the (keyed) attributes and purpose may take part in a decision shared between users. The analysis is conservative: every
/// identifier it does not positively recognise makes the rule user dependent.
/// </summary>
internal static partial class PolicyShapeAnalyzer
{
    /// <summary>Result of analysing one rule.</summary>
    /// <param name="Shareable">No input that identifies the user (or IP or clock) is read.</param>
    /// <param name="ReadsContext">Attributes or purpose are read, so they must be part of a shared key.</param>
    internal readonly record struct RuleShape(bool Shareable, bool ReadsContext);

    private static readonly BoundedCache<(string SubRule, string? RlsFilter), RuleShape> Cache = new(20_000);

    [GeneratedRegex("\"[^\"]*\"|'[^']*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\$\{([^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    // Context members that are derived from the keyed inputs only (attributes, purpose, tenant, table, requested columns).
    private static readonly string[] SharedContextMembers =
    [
        "department", "region", "clearancelevel", "purposeid", "attributes", "getattribute", "tenant", "targettable",
        "requestedcolumns", "tablecolumns", "targetdialect"
    ];

    internal static RuleShape Analyze(string? subRule, string? rlsFilter)
    {
        var key = (subRule ?? string.Empty, rlsFilter);
        if (Cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var shape = Compute(subRule, rlsFilter);
        Cache.Set(key, shape);
        return shape;
    }

    private static RuleShape Compute(string? subRule, string? rlsFilter)
    {
        // REQ-GOV-129: a rule that mentions a caller attribute placeholder can never be shared between users (it is refused at publication anyway).
        if (Autheris.Application.VirtualFilters.CallerPlaceholders.Contains(subRule) || Autheris.Application.VirtualFilters.CallerPlaceholders.Contains(rlsFilter))
        {
            return new RuleShape(false, false);
        }

        var readsContext = false;

        if (!string.IsNullOrWhiteSpace(subRule))
        {
            var stripped = StringLiteral().Replace(subRule, " ");
            foreach (Match match in Identifier().Matches(stripped))
            {
                var chain = match.Value.ToLowerInvariant();
                if (chain is "true" or "false" or "null")
                {
                    continue;
                }

                var parts = chain.Split('.');
                var index = parts[0] == "r" ? 1 : 0;
                if (index == 1 && parts.Length >= 2 && parts[1] is "tenant" or "obj" or "act")
                {
                    continue;
                }

                if (parts.Length > index + 1 && parts[index] is "ctx" or "context" && Array.IndexOf(SharedContextMembers, parts[index + 1]) >= 0)
                {
                    readsContext = true;
                    continue;
                }

                return new RuleShape(false, false);
            }
        }

        if (!string.IsNullOrWhiteSpace(rlsFilter))
        {
            foreach (Match match in Placeholder().Matches(rlsFilter))
            {
                var name = match.Groups[1].Value.Trim().ToLowerInvariant();
                switch (name)
                {
                    case "r.sub":
                    case "user_sid":
                        return new RuleShape(false, false);
                    case "r.tenant":
                    case "tenant":
                        break;
                    default:
                        // department, region, clearance, purpose and every other name resolve from the attributes or the purpose.
                        readsContext = true;
                        break;
                }
            }
        }

        return new RuleShape(true, readsContext);
    }
}
