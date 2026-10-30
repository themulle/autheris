namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Issue #392: a relation collected while the manifest nodes are read; resolved after the nodes and sources were indexed
/// (test nodes precede sources in a manifest). Memory is bounded because every entry is counted when it is collected.
/// </summary>
internal sealed record DbtPendingRelation(
    string To,
    string? AttachedNode,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> ParentColumns,
    IReadOnlyList<string> ChildColumns,
    string? RelationName,
    string? OwnerUniqueId,
    string OwnerName);

internal readonly record struct DbtRelationResolution(
    string? ParentUniqueId,
    string? ChildUniqueId,
    string ParentName,
    string ChildName,
    string? ErrorCode);

internal static class DbtRelationReferenceResolution
{
    /// <summary>Upper bound of <c>depends_on</c> entries read per test node.</summary>
    public const int MaxDependsOn = 1024;

    private static readonly string[] EligiblePrefixes = ["model.", "seed.", "snapshot.", "source."];

    public static bool IsEligible(string uniqueId)
    {
        foreach (var prefix in EligiblePrefixes)
        {
            if (uniqueId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Distinct eligible nodes of a <c>depends_on</c> list (I392-SC-04), in order.</summary>
    public static List<string> DistinctEligible(IReadOnlyList<string> dependsOn)
    {
        var result = new List<string>();
        foreach (var id in dependsOn)
        {
            if (!string.IsNullOrWhiteSpace(id) && IsEligible(id) && !result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }

        return result;
    }

    /// <summary>Reads the <c>depends_on.nodes</c> strings of a node, bounded by <see cref="MaxDependsOn"/> (I392-SC-08).</summary>
    public static List<string> ReadDependsOn(JsonElement node)
    {
        var result = new List<string>();
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("depends_on", out var depends) || depends.ValueKind != JsonValueKind.Object ||
            !depends.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in nodes.EnumerateArray())
        {
            // Only eligible ids are kept (the resolver and the attached-node check never need others).
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id && IsEligible(id))
            {
                result.Add(id);
                if (result.Count >= MaxDependsOn)
                {
                    break;
                }
            }
        }

        return result;
    }

    public static DbtRelationResolution Resolve(DbtPendingRelation pending, DbtManifestReferenceResolver resolver)
    {
        var parent = resolver.Resolve(pending.To, pending.DependsOn);
        var parentName = parent.UniqueId is not null ? resolver.NameOf(parent.UniqueId) ?? parent.LogicalName : parent.LogicalName;
        if (parent.Outcome != DbtReferenceOutcome.Resolved || parent.UniqueId is null)
        {
            return new DbtRelationResolution(null, null, parentName, pending.OwnerName,
                parent.Outcome == DbtReferenceOutcome.Ambiguous ? "AMBIGUOUS_REFERENCE" : "UNKNOWN_MODEL");
        }

        string? childId;
        if (pending.OwnerUniqueId is not null)
        {
            childId = pending.OwnerUniqueId;
        }
        else if (!string.IsNullOrWhiteSpace(pending.AttachedNode))
        {
            // I392-SC-03: the attached node must be indexed and listed in depends_on.
            childId = pending.AttachedNode;
            if (!pending.DependsOn.Contains(childId, StringComparer.Ordinal))
            {
                return Fail(parent.UniqueId, parentName, "UNKNOWN_MODEL");
            }
        }
        else
        {
            var eligible = DistinctEligible(pending.DependsOn);
            var others = eligible.FindAll(id => !string.Equals(id, parent.UniqueId, StringComparison.Ordinal));
            if (others.Count == 1)
            {
                childId = others[0];
            }
            else if (others.Count == 0 && eligible.Count == 1)
            {
                // I392-SC-04: self relation only when depends_on holds exactly the parent.
                childId = parent.UniqueId;
            }
            else
            {
                return Fail(parent.UniqueId, parentName, eligible.Count == 0 ? "UNKNOWN_MODEL" : "AMBIGUOUS_REFERENCE");
            }
        }

        if (!resolver.Contains(childId))
        {
            return Fail(parent.UniqueId, parentName, "UNKNOWN_MODEL");
        }

        return new DbtRelationResolution(parent.UniqueId, childId, parentName, resolver.NameOf(childId) ?? pending.OwnerName, null);

        DbtRelationResolution Fail(string parentId, string pName, string code) =>
            new(parentId, null, pName, pending.OwnerName, code);
    }
}
