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
        ArgumentNullException.ThrowIfNull(stream);

        var models = new List<DbtModelDefinition>();

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

        return models;
    }
}
