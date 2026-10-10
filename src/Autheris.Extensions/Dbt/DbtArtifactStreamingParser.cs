using Autheris.Domain.Common;
namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public static class DbtArtifactStreamingParser
{
    public static async Task<IReadOnlyList<DbtModelDefinition>> ParseManifestStreamAsync(Stream stream, CancellationToken ct = default)
    {
        var result = await ParseManifestWithRelationshipsAsync(stream, ct).ConfigureAwait(false);
        return result.Models;
    }

    public static async Task<(IReadOnlyList<DbtModelDefinition> Models, IReadOnlyList<DbtRelationshipDefinition> Relationships)> ParseManifestWithRelationshipsAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var models = new List<DbtModelDefinition>();
        var relationships = new List<DbtRelationshipDefinition>();

        // Parse JsonDocument asynchronously using streaming options
        var jsonDocOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        };

        using var document = await JsonDocument.ParseAsync(stream, jsonDocOptions, ct).ConfigureAwait(false);
        var root = document.RootElement;

        if (root.TryGetProperty("nodes", out var nodesElement) && nodesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var nodeProperty in nodesElement.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();
                var node = nodeProperty.Value;

                // R-31: Inspect test nodes for relationship tests
                if (nodeProperty.Name.StartsWith("test.", StringComparison.OrdinalIgnoreCase))
                {
                    if (node.TryGetProperty("test_metadata", out var tmProp) &&
                        tmProp.TryGetProperty("name", out var tmNameProp) &&
                        string.Equals(tmNameProp.GetString(), "relationships", StringComparison.OrdinalIgnoreCase))
                    {
                        if (tmProp.TryGetProperty("kwargs", out var kwProp))
                        {
                            var childCol = kwProp.TryGetProperty("column_name", out var ccProp) ? ccProp.GetString() ?? "" : "";
                            var parentRef = kwProp.TryGetProperty("to", out var toProp) ? toProp.GetString() ?? "" : "";
                            var parentCol = kwProp.TryGetProperty("field", out var fProp) ? fProp.GetString() ?? "" : "";

                            var cleanParent = ExtractModelName(parentRef);
                            string childModel = "";
                            if (node.TryGetProperty("attached_node", out var anProp) && anProp.GetString() is string an && !string.IsNullOrWhiteSpace(an))
                            {
                                childModel = ExtractModelName(an);
                            }
                            else if (node.TryGetProperty("depends_on", out var depProp) &&
                                     depProp.TryGetProperty("nodes", out var depNodes) &&
                                     depNodes.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var dn in depNodes.EnumerateArray())
                                {
                                    var dnStr = dn.GetString() ?? "";
                                    if (dnStr.StartsWith("model.", StringComparison.OrdinalIgnoreCase) ||
                                        dnStr.StartsWith("source.", StringComparison.OrdinalIgnoreCase) ||
                                        dnStr.StartsWith("seed.", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var mName = ExtractModelName(dnStr);
                                        if (!string.Equals(mName, cleanParent, StringComparison.OrdinalIgnoreCase))
                                        {
                                            childModel = mName;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(cleanParent) && !string.IsNullOrWhiteSpace(childModel) &&
                                !string.IsNullOrWhiteSpace(childCol) && !string.IsNullOrWhiteSpace(parentCol))
                            {
                                var relName = cleanParent.StartsWith("stg_") ? cleanParent[4..] : cleanParent;
                                relationships.Add(new DbtRelationshipDefinition(
                                    relName,
                                    cleanParent,
                                    childModel,
                                    parentCol,
                                    childCol
                                ));
                            }
                        }
                    }
                    continue;
                }

                // Only inspect model and seed nodes (e.g. "model.my_project.customers")
                if (!nodeProperty.Name.StartsWith("model.", StringComparison.OrdinalIgnoreCase) &&
                    !nodeProperty.Name.StartsWith("seed.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var uniqueId = nodeProperty.Name;
                var name = node.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var database = node.TryGetProperty("database", out var dbProp) ? dbProp.GetString() ?? "" : "";
                var schema = node.TryGetProperty("schema", out var schemaProp) ? schemaProp.GetString() ?? "" : "";
                var description = node.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;

                // Materialization
                var materialization = "table";
                if (node.TryGetProperty("config", out var configProp) &&
                    configProp.TryGetProperty("materialized", out var matProp))
                {
                    materialization = matProp.GetString() ?? "table";
                }

                // Contract Enforced
                var contractEnforced = false;
                if (node.TryGetProperty("contract", out var contractProp) &&
                    contractProp.TryGetProperty("enforced", out var enfProp) &&
                    enfProp.ValueKind == JsonValueKind.True)
                {
                    contractEnforced = true;
                }

                // Tags
                var tags = new List<string>();
                if (node.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tagsProp.EnumerateArray())
                    {
                        var tagStr = tag.GetString();
                        if (!string.IsNullOrWhiteSpace(tagStr))
                        {
                            tags.Add(tagStr);
                        }
                    }
                }

                // Meta
                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (node.TryGetProperty("meta", out var metaProp) && metaProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var m in metaProp.EnumerateObject())
                    {
                        meta[m.Name] = JsonValueText.From(m.Value);
                    }
                }

                // R-31: Model-level constraints (foreign_key)
                if (node.TryGetProperty("constraints", out var modelConsProp) && modelConsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var con in modelConsProp.EnumerateArray())
                    {
                        if (con.TryGetProperty("type", out var typeProp) &&
                            string.Equals(typeProp.GetString(), "foreign_key", StringComparison.OrdinalIgnoreCase))
                        {
                            var toModel = con.TryGetProperty("to", out var toP) ? ExtractModelName(toP.GetString() ?? "") : "";
                            string parentCol = "id";
                            if (con.TryGetProperty("to_columns", out var toCols) && toCols.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var tc in toCols.EnumerateArray()) { parentCol = tc.GetString() ?? "id"; break; }
                            }
                            string childCol = "id";
                            if (con.TryGetProperty("columns", out var fromCols) && fromCols.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var fc in fromCols.EnumerateArray()) { childCol = fc.GetString() ?? "id"; break; }
                            }

                            if (!string.IsNullOrWhiteSpace(toModel))
                            {
                                var relName = toModel.StartsWith("stg_") ? toModel[4..] : toModel;
                                relationships.Add(new DbtRelationshipDefinition(relName, toModel, name, parentCol, childCol));
                            }
                        }
                    }
                }

                // Columns
                var columns = new Dictionary<string, DbtColumnDefinition>(StringComparer.OrdinalIgnoreCase);
                if (node.TryGetProperty("columns", out var colsProp) && colsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var colProp in colsProp.EnumerateObject())
                    {
                        var colName = colProp.Name;
                        var colVal = colProp.Value;
                        var colType = colVal.TryGetProperty("data_type", out var dtProp) ? dtProp.GetString() : null;
                        var colDesc = colVal.TryGetProperty("description", out var cdProp) ? cdProp.GetString() : null;

                        var colTags = new List<string>();
                        if (colVal.TryGetProperty("tags", out var ctProp) && ctProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var ctItem in ctProp.EnumerateArray())
                            {
                                var s = ctItem.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) colTags.Add(s);
                            }
                        }

                        var colMeta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (colVal.TryGetProperty("meta", out var cmProp) && cmProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var cm in cmProp.EnumerateObject())
                            {
                                colMeta[cm.Name] = JsonValueText.From(cm.Value);
                            }
                        }

                        // R-31: Column-level constraints (foreign_key)
                        if (colVal.TryGetProperty("constraints", out var colConsProp) && colConsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var con in colConsProp.EnumerateArray())
                            {
                                if (con.TryGetProperty("type", out var typeProp) &&
                                    string.Equals(typeProp.GetString(), "foreign_key", StringComparison.OrdinalIgnoreCase))
                                {
                                    var toModel = con.TryGetProperty("to", out var toP) ? ExtractModelName(toP.GetString() ?? "") : "";
                                    string parentCol = "id";
                                    if (con.TryGetProperty("field", out var fP) && !string.IsNullOrWhiteSpace(fP.GetString()))
                                    {
                                        parentCol = fP.GetString()!;
                                    }
                                    else if (con.TryGetProperty("to_columns", out var tcP) && tcP.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var tc in tcP.EnumerateArray()) { if (!string.IsNullOrWhiteSpace(tc.GetString())) { parentCol = tc.GetString()!; break; } }
                                    }
                                    if (!string.IsNullOrWhiteSpace(toModel))
                                    {
                                        var relName = toModel.StartsWith("stg_") ? toModel[4..] : toModel;
                                        relationships.Add(new DbtRelationshipDefinition(relName, toModel, name, parentCol, colName));
                                    }
                                }
                            }
                        }

                        columns[colName] = new DbtColumnDefinition(colName, colType, colDesc, colTags, colMeta);
                    }
                }

                // Depends On
                var dependsOn = new List<string>();
                if (node.TryGetProperty("depends_on", out var dependsProp) &&
                    dependsProp.TryGetProperty("nodes", out var depNodesProp) &&
                    depNodesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var depNode in depNodesProp.EnumerateArray())
                    {
                        var s = depNode.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) dependsOn.Add(s);
                    }
                }

                models.Add(new DbtModelDefinition(
                    uniqueId,
                    name,
                    database,
                    schema,
                    materialization,
                    description,
                    tags,
                    meta,
                    columns,
                    dependsOn,
                    contractEnforced
                ));
            }
        }

        // B-04: Support source tables from dbt manifest
        if (root.TryGetProperty("sources", out var sourcesElement) && sourcesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var srcProp in sourcesElement.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();
                var srcNode = srcProp.Value;
                var uniqueId = srcProp.Name;
                var name = srcNode.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                var database = srcNode.TryGetProperty("database", out var dbp) ? dbp.GetString() ?? "" : "";
                var schema = srcNode.TryGetProperty("schema", out var sp) ? sp.GetString() ?? "" : "";
                var description = srcNode.TryGetProperty("description", out var dp) ? dp.GetString() : null;

                var tags = new List<string>();
                if (srcNode.TryGetProperty("tags", out var tp) && tp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tp.EnumerateArray())
                    {
                        var s = tag.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) tags.Add(s);
                    }
                }

                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (srcNode.TryGetProperty("meta", out var mp) && mp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var m in mp.EnumerateObject())
                    {
                        meta[m.Name] = JsonValueText.From(m.Value);
                    }
                }

                var columns = new Dictionary<string, DbtColumnDefinition>(StringComparer.OrdinalIgnoreCase);
                if (srcNode.TryGetProperty("columns", out var cp) && cp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var colProp in cp.EnumerateObject())
                    {
                        var colName = colProp.Name;
                        var colVal = colProp.Value;
                        var colType = colVal.TryGetProperty("data_type", out var dtp) ? dtp.GetString() : null;
                        var colDesc = colVal.TryGetProperty("description", out var cdp) ? cdp.GetString() : null;
                        var colTags = new List<string>();
                        if (colVal.TryGetProperty("tags", out var ctp) && ctp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in ctp.EnumerateArray()) if (item.GetString() is string s) colTags.Add(s);
                        }
                        var colMeta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (colVal.TryGetProperty("meta", out var cmp) && cmp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var cm in cmp.EnumerateObject()) colMeta[cm.Name] = JsonValueText.From(cm.Value);
                        }
                        columns[colName] = new DbtColumnDefinition(colName, colType, colDesc, colTags, colMeta);
                    }
                }

                models.Add(new DbtModelDefinition(
                    uniqueId,
                    name,
                    database,
                    schema,
                    "source",
                    description,
                    tags,
                    meta,
                    columns,
                    [],
                    false
                ));
            }
        }

        return (models, relationships);
    }

    public static string ExtractModelName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();
        var refMatch = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"ref\s*\(\s*['""]([^'""]+)['""]\s*\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (refMatch.Success)
        {
            return refMatch.Groups[1].Value;
        }

        // B-04: Support source('source_name', 'table_name')
        var sourceMatch = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"source\s*\(\s*['""]([^'""]+)['""]\s*,\s*['""]([^'""]+)['""]\s*\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (sourceMatch.Success)
        {
            return $"{sourceMatch.Groups[1].Value}.{sourceMatch.Groups[2].Value}";
        }

        var parts = trimmed.Split('.');
        return parts[^1];
    }
}
