namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Parsing of the virtual filters and access profiles of a dbt governance import payload (moved out of the main file unchanged).</summary>
public sealed partial class DbtMetadataIngestionService
{
    private static List<VirtualFilter> ParseVirtualFilters(JsonElement element, TenantId defaultTenant)
    {
        var result = new List<VirtualFilter>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var filter = ParseSingleFilter(prop.Name, prop.Value, defaultTenant);
                if (filter != null) result.Add(filter);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var filter = ParseSingleFilter(name, item, defaultTenant);
                if (filter != null) result.Add(filter);
            }
        }
        return result;
    }

    private static VirtualFilter? ParseSingleFilter(string name, JsonElement val, TenantId defaultTenant)
    {
        // A missing tenant is the caller's request tenant (never the legacy tenant); the caller checks explicit ones.
        var tenantStr = val.TryGetProperty("tenant", out var tProp) ? tProp.GetString() : null;
        var tenant = !string.IsNullOrWhiteSpace(tenantStr) ? new TenantId(tenantStr) : defaultTenant;
        var source = val.TryGetProperty("source", out var sProp) ? sProp.GetString() ?? "" : "";
        var sql = val.TryGetProperty("sql", out var sqlProp) ? sqlProp.GetString() : null;
        var validFrom = val.TryGetProperty("valid_from", out var vfProp) ? vfProp.GetString() : null;
        var validTo = val.TryGetProperty("valid_to", out var vtProp) ? vtProp.GetString() : null;

        var keyCols = new List<string>();
        if (val.TryGetProperty("key_columns", out var kcProp) && kcProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var k in kcProp.EnumerateArray()) if (k.GetString() is string s) keyCols.Add(s);
        }

        var supersedes = new List<string>();
        if (val.TryGetProperty("supersedes", out var supProp) && supProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in supProp.EnumerateArray()) if (s.GetString() is string str) supersedes.Add(str);
        }

        StructuredFilterDefinition? structured = null;
        if (val.TryGetProperty("from", out var fromProp) && fromProp.ValueKind == JsonValueKind.Object)
        {
            var fromTableStr = fromProp.TryGetProperty("table", out var ftProp) ? ftProp.GetString() ?? "" : "";
            var fromAlias = fromProp.TryGetProperty("alias", out var faProp) ? faProp.GetString() ?? "" : "";
            var parts = fromTableStr.Split('.');
            var fromTable = parts.Length == 2 ? new TableIdentifier(source, parts[0], parts[1]) : new TableIdentifier(source, "dbo", fromTableStr);

            var joins = new List<FilterJoin>();
            if (val.TryGetProperty("joins", out var joinsProp) && joinsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var j in joinsProp.EnumerateArray())
                {
                    var jTableStr = j.TryGetProperty("table", out var jt) ? jt.GetString() ?? "" : "";
                    var jAlias = j.TryGetProperty("alias", out var ja) ? ja.GetString() ?? "" : "";
                    var jLeft = j.TryGetProperty("left", out var jl) ? jl.GetString() ?? "" : "";
                    var jRight = j.TryGetProperty("right", out var jr) ? jr.GetString() ?? "" : "";
                    var jParts = jTableStr.Split('.');
                    var jTable = jParts.Length == 2 ? new TableIdentifier(source, jParts[0], jParts[1]) : new TableIdentifier(source, "dbo", jTableStr);
                    joins.Add(new FilterJoin(jTable, jAlias, jLeft, jRight));
                }
            }

            var whereConditions = new List<FilterCondition>();
            if (val.TryGetProperty("where", out var whereProp) && whereProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in whereProp.EnumerateArray())
                {
                    var col = w.TryGetProperty("column", out var wc) ? wc.GetString() ?? "" : "";
                    var opStr = w.TryGetProperty("op", out var wo) ? wo.GetString() ?? "eq" : "eq";
                    var wVal = w.TryGetProperty("value", out var wv) ? wv.GetString() : null;
                    var op = opStr.ToLowerInvariant() switch
                    {
                        "eq" => FilterConditionOperator.Eq,
                        "neq" => FilterConditionOperator.NotEq,
                        "is_null" => FilterConditionOperator.IsNull,
                        _ => FilterConditionOperator.IsNotNull
                    };
                    whereConditions.Add(new FilterCondition(col, op, wVal));
                }
            }

            structured = new StructuredFilterDefinition
            {
                From = fromTable,
                FromAlias = fromAlias,
                Joins = joins,
                Where = whereConditions
            };
        }

        return new VirtualFilter
        {
            TenantId = tenant,
            Name = name,
            Source = source,
            Sql = sql,
            Structured = structured,
            KeyColumns = keyCols,
            ValidFromColumn = validFrom,
            ValidToColumn = validTo,
            Supersedes = supersedes
        };
    }

    private static List<VirtualFilterAccessProfile> ParseAccessProfiles(JsonElement element, TenantId defaultTenant)
    {
        var result = new List<VirtualFilterAccessProfile>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var profile = ParseSingleProfile(prop.Name, prop.Value, defaultTenant);
                if (profile != null) result.Add(profile);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var profile = ParseSingleProfile(name, item, defaultTenant);
                if (profile != null) result.Add(profile);
            }
        }
        return result;
    }

    private static VirtualFilterAccessProfile? ParseSingleProfile(string name, JsonElement val, TenantId defaultTenant)
    {
        // A missing tenant is the caller's request tenant (never the legacy tenant); the caller checks explicit ones.
        var tenantStr = val.TryGetProperty("tenant", out var tProp) ? tProp.GetString() : null;
        var tenant = !string.IsNullOrWhiteSpace(tenantStr) ? new TenantId(tenantStr) : defaultTenant;
        var scope = val.TryGetProperty("scope", out var scProp) ? scProp.GetString() ?? "*" : "*";

        UncoveredPolicy? uncovered = null;
        if (val.TryGetProperty("uncovered", out var uProp) && uProp.GetString() is string uStr)
        {
            uncovered = string.Equals(uStr, "skip", StringComparison.OrdinalIgnoreCase) ? UncoveredPolicy.Skip : UncoveredPolicy.Deny;
        }

        GranteeType granteeType = GranteeType.User;
        Sid? granteeSid = null;
        string? roleName = null;

        if (val.TryGetProperty("grantee", out var gProp) && gProp.ValueKind == JsonValueKind.Object)
        {
            var gTypeStr = gProp.TryGetProperty("type", out var gt) ? gt.GetString() : "user";
            granteeType = (gTypeStr?.ToLowerInvariant()) switch
            {
                "group" => GranteeType.Group,
                "role" => GranteeType.Role,
                "serviceprincipal" => GranteeType.ServicePrincipal,
                _ => GranteeType.User
            };
            if (gProp.TryGetProperty("sid", out var gs) && gs.GetString() is string s)
            {
                granteeSid = new Sid(s);
            }
            if (gProp.TryGetProperty("role", out var gr) && gr.GetString() is string r)
            {
                roleName = r;
            }
        }

        var bindings = new List<FilterBinding>();
        if (val.TryGetProperty("bindings", out var bProp) && bProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in bProp.EnumerateArray())
            {
                var fName = b.TryGetProperty("filter", out var fn) ? fn.GetString() ?? "" : "";
                var target = b.TryGetProperty("target", out var tgt) ? tgt.GetString() : null;
                var timeCol = b.TryGetProperty("time_column", out var tc) ? tc.GetString() : null;
                Dictionary<string, string>? map = null;
                if (b.TryGetProperty("map", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in m.EnumerateObject())
                    {
                        if (kv.Value.GetString() is string mv) map[kv.Name] = mv;
                    }
                }
                bindings.Add(new FilterBinding
                {
                    FilterName = fName,
                    TargetPattern = target,
                    TimeColumn = timeCol,
                    ColumnMap = map
                });
            }
        }

        return new VirtualFilterAccessProfile
        {
            TenantId = tenant,
            Name = name,
            GranteeType = granteeType,
            GranteeSid = granteeSid,
            RoleName = roleName,
            Scope = scope,
            Uncovered = uncovered,
            Bindings = bindings
        };
    }

    private static ImmutableArray<string>? ParseMetaRequiredFilterColumns(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta == null) return null;
        if (!meta.TryGetValue("required_filter_columns", out var val) &&
            !meta.TryGetValue("requiredFilterColumns", out val))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(val)) return ImmutableArray<string>.Empty;

        try
        {
            using var doc = JsonDocument.Parse(val);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
                return list.ToImmutableArray();
            }
        }
        catch (JsonException)
        {
            // fallback to comma-separated
        }

        var parts = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.ToImmutableArray();
    }

    private static ImmutableArray<ImmutableArray<string>>? ParseMetaRequiredFilterColumnGroups(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta == null) return null;
        if (!meta.TryGetValue("required_filter_column_groups", out var val) &&
            !meta.TryGetValue("requiredFilterColumnGroups", out val))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(val)) return ImmutableArray<ImmutableArray<string>>.Empty;

        try
        {
            using var doc = JsonDocument.Parse(val);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var outer = new List<ImmutableArray<string>>();
                foreach (var groupEl in doc.RootElement.EnumerateArray())
                {
                    if (groupEl.ValueKind == JsonValueKind.Array)
                    {
                        var inner = new List<string>();
                        foreach (var el in groupEl.EnumerateArray())
                        {
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) inner.Add(s);
                        }
                        outer.Add(inner.ToImmutableArray());
                    }
                }
                return outer.ToImmutableArray();
            }
        }
        catch (JsonException)
        {
            // invalid format
        }

        return ImmutableArray<ImmutableArray<string>>.Empty;
    }

    /// <summary>Stored required filter columns (a conjunction) missing from the incoming set; each one is a relaxation.</summary>
    private static List<string> RemovedRequiredFilterColumns(ImmutableArray<string> stored, ImmutableArray<string> incoming)
    {
        var incomingSet = new HashSet<string>(incoming.IsDefault ? ImmutableArray<string>.Empty : incoming, StringComparer.OrdinalIgnoreCase);
        return (stored.IsDefault ? ImmutableArray<string>.Empty : stored).Where(c => !incomingSet.Contains(c)).ToList();
    }

    /// <summary>
    /// Required filter column groups are alternatives (any group satisfies the requirement). Relaxations: a stored group that is
    /// missing from the incoming groups (also when only its columns changed), and, when groups are stored, an incoming group that
    /// is not at least as strict as (a superset of) a stored group, because it adds a weaker alternative.
    /// </summary>
    private static List<string> RelaxedRequiredFilterColumnGroups(ImmutableArray<ImmutableArray<string>> stored, ImmutableArray<ImmutableArray<string>> incoming)
    {
        static List<(string Text, HashSet<string> Set)> ToSets(ImmutableArray<ImmutableArray<string>> groups) =>
            (groups.IsDefault ? ImmutableArray<ImmutableArray<string>>.Empty : groups)
                .Select(g => g.IsDefault ? ImmutableArray<string>.Empty : g)
                .Select(g => (string.Join(", ", g), new HashSet<string>(g, StringComparer.OrdinalIgnoreCase)))
                .ToList();

        var storedSets = ToSets(stored);
        var incomingSets = ToSets(incoming);
        var result = new List<string>();
        foreach (var (text, set) in storedSets)
        {
            if (!incomingSets.Any(i => i.Set.SetEquals(set)))
            {
                result.Add($"removing required filter column group [{text}]");
            }
        }

        if (storedSets.Count > 0)
        {
            foreach (var (text, set) in incomingSets)
            {
                if (!storedSets.Any(s => set.IsSupersetOf(s.Set)))
                {
                    result.Add($"adding the weaker required filter column group [{text}]");
                }
            }
        }

        return result;
    }

    private static ImmutableArray<string>? ParseJsonRequiredFilterColumns(JsonElement item)
    {
        if (!item.TryGetProperty("required_filter_columns", out var prop) &&
            !item.TryGetProperty("requiredFilterColumns", out prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var el in prop.EnumerateArray())
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            return list.ToImmutableArray();
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var val = prop.GetString();
            if (string.IsNullOrWhiteSpace(val)) return ImmutableArray<string>.Empty;
            return val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();
        }

        return ImmutableArray<string>.Empty;
    }

    private static ImmutableArray<ImmutableArray<string>>? ParseJsonRequiredFilterColumnGroups(JsonElement item)
    {
        if (!item.TryGetProperty("required_filter_column_groups", out var prop) &&
            !item.TryGetProperty("requiredFilterColumnGroups", out prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            var outer = new List<ImmutableArray<string>>();
            foreach (var groupEl in prop.EnumerateArray())
            {
                if (groupEl.ValueKind == JsonValueKind.Array)
                {
                    var inner = new List<string>();
                    foreach (var el in groupEl.EnumerateArray())
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) inner.Add(s);
                    }
                    outer.Add(inner.ToImmutableArray());
                }
            }
            return outer.ToImmutableArray();
        }

        return ImmutableArray<ImmutableArray<string>>.Empty;
    }
}
