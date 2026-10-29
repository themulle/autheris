namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>Issue #392: collects <c>relationships</c> and <c>relationships_combination</c> tests and resolves pending relations.</summary>
internal static class DbtRelationImport
{
    private const string Unresolved = "?";

    public static void CollectTest(JsonElement node, JsonElement root, RelationCollector relations, List<DbtPendingRelation> pending)
    {
        if (!node.TryGetProperty("test_metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var testName = nameProp.GetString();
        var isSingle = string.Equals(testName, "relationships", StringComparison.OrdinalIgnoreCase);
        var isCombination = string.Equals(testName, "relationships_combination", StringComparison.OrdinalIgnoreCase);
        if ((!isSingle && !isCombination) || !metadata.TryGetProperty("kwargs", out var kwargs) || kwargs.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var attached = ReadString(node, "attached_node");
        if (isSingle)
        {
            var childColumn = ReadString(kwargs, "column_name");
            var to = ReadString(kwargs, "to");
            var parentColumn = ReadString(kwargs, "field");
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(childColumn) || !relations.Reserve())
            {
                return;
            }

            pending.Add(new DbtPendingRelation(
                to, attached, DbtRelationReferenceResolution.ReadDependsOn(node),
                string.IsNullOrWhiteSpace(parentColumn) ? [] : [parentColumn], [childColumn], null, null, Unresolved));
            return;
        }

        if (!relations.Reserve())
        {
            return;
        }

        // I392-SC-08: only the project's own (or no) namespace; a package test of the same name may mean something else.
        var ownPackage = root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? ReadString(meta, "project_name")
            : null;
        ownPackage ??= ReadString(node, "package_name");
        var ns = metadata.TryGetProperty("namespace", out var nsProp) ? nsProp : default;
        var namespaceOk = ns.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
                          (ns.ValueKind == JsonValueKind.String &&
                           (string.IsNullOrEmpty(ns.GetString()) || string.Equals(ns.GetString(), ownPackage, StringComparison.OrdinalIgnoreCase)));
        var relationName = kwargs.TryGetProperty("relation_name", out var rn) ? rn : default;
        var to2 = ReadString(kwargs, "to");
        if (!namespaceOk || string.IsNullOrWhiteSpace(to2) ||
            !kwargs.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array ||
            !kwargs.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array ||
            relationName.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
        {
            relations.Skip(Unresolved, Unresolved, Unresolved, "UNSUPPORTED_FORM", reserved: true);
            return;
        }

        pending.Add(new DbtPendingRelation(
            to2, attached, DbtRelationReferenceResolution.ReadDependsOn(node),
            DbtArtifactStreamingParser.ReadKeyArray(kwargs, "fields"), DbtArtifactStreamingParser.ReadKeyArray(kwargs, "columns"),
            relationName.ValueKind == JsonValueKind.String ? relationName.GetString() : null, null, Unresolved));
    }

    /// <summary>Resolves every pending relation in manifest order; unresolved or ambiguous ones are skipped with a reason code.</summary>
    public static void ResolveAll(List<DbtPendingRelation> pending, DbtManifestReferenceResolver resolver, RelationCollector relations)
    {
        foreach (var p in pending)
        {
            var r = DbtRelationReferenceResolution.Resolve(p, resolver);
            var name = p.RelationName ?? (r.ParentName.StartsWith("stg_", StringComparison.Ordinal) ? r.ParentName[4..] : r.ParentName);
            if (r.ErrorCode is not null || r.ParentUniqueId is null || r.ChildUniqueId is null)
            {
                relations.Skip(name, r.ChildName, r.ParentName, r.ErrorCode ?? "UNKNOWN_MODEL", reserved: true);
                continue;
            }

            relations.TryAdd(name, r.ParentName, r.ChildName, p.ParentColumns, p.ChildColumns, reserved: true, r.ParentUniqueId, r.ChildUniqueId);
        }
    }

    private static string? ReadString(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
