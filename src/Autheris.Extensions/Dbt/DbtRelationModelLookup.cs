namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Issue #392: fail-closed model lookup for relation import (I392-SC-01, I392-SC-03). A relation with unique ids resolves by exact
/// unique id only; a legacy relation (no ids) resolves by name only when exactly one model carries it. Never first-wins or last-wins.
/// </summary>
internal sealed class DbtRelationModelLookup
{
    private readonly Dictionary<string, List<DbtModelDefinition>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DbtModelDefinition>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public DbtRelationModelLookup(IEnumerable<DbtModelDefinition> models)
    {
        foreach (var m in models)
        {
            Add(_byId, m.UniqueId, m);
            Add(_byName, m.Name, m);
            Add(_byName, m.UniqueId, m);
        }
    }

    /// <summary>Resolves both sides; returns null and the reason code (UNKNOWN_MODEL or AMBIGUOUS_MODEL) when either side fails.</summary>
    public string? TryResolve(DbtRelationshipDefinition rel, out DbtModelDefinition? parent, out DbtModelDefinition? child)
    {
        parent = null;
        child = null;
        var hasParentId = !string.IsNullOrWhiteSpace(rel.ParentUniqueId);
        var hasChildId = !string.IsNullOrWhiteSpace(rel.ChildUniqueId);
        if (hasParentId != hasChildId)
        {
            return "UNKNOWN_MODEL";
        }

        var (parentCode, parentModel) = hasParentId ? ById(rel.ParentUniqueId!) : ByName(rel.ParentModelOrTable);
        var (childCode, childModel) = hasChildId ? ById(rel.ChildUniqueId!) : ByName(rel.ChildModelOrTable);
        if (parentCode is not null || childCode is not null)
        {
            // Ambiguity is reported whenever one side is ambiguous, otherwise unknown.
            return parentCode == "AMBIGUOUS_MODEL" || childCode == "AMBIGUOUS_MODEL" ? "AMBIGUOUS_MODEL" : "UNKNOWN_MODEL";
        }

        parent = parentModel;
        child = childModel;
        return null;
    }

    private (string? Code, DbtModelDefinition? Model) ById(string id) =>
        _byId.TryGetValue(id, out var list) ? Single(list) : ("UNKNOWN_MODEL", null);

    private (string? Code, DbtModelDefinition? Model) ByName(string name) =>
        _byName.TryGetValue(name, out var list) ? Single(list) : ("UNKNOWN_MODEL", null);

    private static (string? Code, DbtModelDefinition? Model) Single(List<DbtModelDefinition> list)
    {
        DbtModelDefinition? first = null;
        foreach (var m in list)
        {
            if (first is null)
            {
                first = m;
            }
            else if (!ReferenceEquals(first, m))
            {
                return ("AMBIGUOUS_MODEL", null);
            }
        }

        return (null, first);
    }

    private static void Add(Dictionary<string, List<DbtModelDefinition>> map, string key, DbtModelDefinition model)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        if (!list.Contains(model))
        {
            list.Add(model);
        }
    }
}
