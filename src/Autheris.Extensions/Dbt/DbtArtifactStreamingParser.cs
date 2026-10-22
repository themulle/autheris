using Autheris.Application.Catalog.Profiling;
using Autheris.Application.DataCatalog.Services;
using Autheris.Domain.Common;
namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    public static async Task<DbtManifestParseResult> ParseManifestWithRelationshipsAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var models = new List<DbtModelDefinition>();
        var relations = new RelationCollector();
        // Issue #94 (D-8): declared keys per model unique id (constraints and unique tests may arrive in any node order).
        var keyBuilders = new Dictionary<string, CatalogKeyDefinitionImport.Collector>(StringComparer.OrdinalIgnoreCase);

        // Parse JsonDocument asynchronously using streaming options
        var jsonDocOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        };

        using var document = await JsonDocument.ParseAsync(stream, jsonDocOptions, ct).ConfigureAwait(false);
        var root = document.RootElement;
        var testToModels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

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
                                !string.IsNullOrWhiteSpace(childCol))
                            {
                                var relName = cleanParent.StartsWith("stg_") ? cleanParent[4..] : cleanParent;
                                relations.TryAdd(
                                    relName,
                                    cleanParent,
                                    childModel,
                                    string.IsNullOrWhiteSpace(parentCol) ? [] : [parentCol],
                                    [childCol]);
                            }
                        }
                    }

                    CollectUniqueTest(node, keyBuilders);

                    // EXT-08: collect test target model mappings for circuit breaker attribution
                    var testUniqueId = nodeProperty.Name;
                    var targetModels = new List<string>();
                    if (node.TryGetProperty("attached_node", out var anProp2) && anProp2.GetString() is string anStr && !string.IsNullOrWhiteSpace(anStr))
                    {
                        targetModels.Add(anStr);
                    }
                    if (node.TryGetProperty("depends_on", out var depProp2) &&
                        depProp2.TryGetProperty("nodes", out var depNodes2) &&
                        depNodes2.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var dn in depNodes2.EnumerateArray())
                        {
                            var dnStr = dn.GetString();
                            if (!string.IsNullOrWhiteSpace(dnStr) &&
                                (dnStr.StartsWith("model.", StringComparison.OrdinalIgnoreCase) ||
                                 dnStr.StartsWith("source.", StringComparison.OrdinalIgnoreCase) ||
                                 dnStr.StartsWith("seed.", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (!targetModels.Contains(dnStr, StringComparer.OrdinalIgnoreCase))
                                {
                                    targetModels.Add(dnStr);
                                }
                            }
                        }
                    }
                    if (targetModels.Count > 0)
                    {
                        testToModels[testUniqueId] = targetModels;
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

                // R-31: Model-level constraints (foreign_key); issue #94: primary_key and unique
                var modelKeys = GetBuilder(keyBuilders, uniqueId);
                if (node.TryGetProperty("constraints", out var modelConsProp) && modelConsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var con in modelConsProp.EnumerateArray())
                    {
                        CollectKeyConstraint(modelKeys, con, columnName: null);
                        if (con.TryGetProperty("type", out var typeProp) &&
                            string.Equals(typeProp.GetString(), "foreign_key", StringComparison.OrdinalIgnoreCase))
                        {
                            var toModel = ReadModelRef(con);
                            var childCols = ReadKeyArray(con, "columns");
                            var parentCols = ReadKeyArray(con, "to_columns");
                            var relName = toModel.StartsWith("stg_") ? toModel[4..] : toModel;
                            if (childCols.Count == 0 && con.TryGetProperty("expression", out _))
                            {
                                relations.Skip(relName, name, toModel, "UNSUPPORTED_FORM");
                            }
                            else if (string.IsNullOrWhiteSpace(toModel))
                            {
                                relations.Skip(relName, name, toModel, "MISSING_TARGET");
                            }
                            else
                            {
                                relations.TryAdd(relName, toModel, name, parentCols, childCols);
                            }
                        }
                    }
                }

                // Columns
                var columns = new Dictionary<string, DbtColumnDefinition>(StringComparer.OrdinalIgnoreCase);
                var columnLevelPrimaryKey = new List<string>();
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
                                CollectKeyConstraint(modelKeys, con, columnName: colName, columnLevelPrimaryKey);
                                if (con.TryGetProperty("type", out var typeProp) &&
                                    string.Equals(typeProp.GetString(), "foreign_key", StringComparison.OrdinalIgnoreCase))
                                {
                                    var toModel = ReadModelRef(con);
                                    var parentCols = new List<string>(1);
                                    if (con.TryGetProperty("field", out var fP) && fP.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fP.GetString()))
                                    {
                                        parentCols.Add(fP.GetString()!);
                                    }
                                    else
                                    {
                                        foreach (var tc in ReadKeyArray(con, "to_columns"))
                                        {
                                            if (!string.IsNullOrWhiteSpace(tc)) { parentCols.Add(tc); break; }
                                        }
                                    }

                                    var relName = toModel.StartsWith("stg_") ? toModel[4..] : toModel;
                                    if (string.IsNullOrWhiteSpace(toModel))
                                    {
                                        relations.Skip(relName, name, toModel, "MISSING_TARGET");
                                    }
                                    else if (parentCols.Count == 0 && con.TryGetProperty("expression", out _))
                                    {
                                        relations.Skip(relName, name, toModel, "UNSUPPORTED_FORM");
                                    }
                                    else
                                    {
                                        relations.TryAdd(relName, toModel, name, parentCols, [colName]);
                                    }
                                }
                            }
                        }

                        columns[colName] = new DbtColumnDefinition(colName, colType, colDesc, colTags, colMeta);
                    }
                }

                if (columnLevelPrimaryKey.Count > 0)
                {
                    modelKeys.AddPrimaryKey(columnLevelPrimaryKey);
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
                ) { SyntheticSamples = ExtractSyntheticSamplesFromNode(node, columns.Keys) });
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
                ) { SyntheticSamples = ExtractSyntheticSamplesFromNode(srcNode, columns.Keys) });
            }
        }

        for (var i = 0; i < models.Count; i++)
        {
            if (keyBuilders.TryGetValue(models[i].UniqueId, out var builder) && ToDeclaredKeys(builder) is { } keys)
            {
                models[i] = models[i] with { DeclaredKeys = keys };
            }
        }

        return new DbtManifestParseResult(models, relations.Relationships, relations.Finish(), testToModels);
    }

    private static string ReadModelRef(JsonElement constraint) =>
        constraint.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String ? ExtractModelName(to.GetString() ?? "") : "";

    /// <summary>
    /// Reads an ordered string array for a relation key. At most <c>MaxKeyColumns + 1</c> elements are read (one more than allowed
    /// is enough to report TOO_WIDE); a non-string element becomes an empty string and is rejected later as invalid.
    /// </summary>
    private static List<string> ReadKeyArray(JsonElement owner, string property)
    {
        var result = new List<string>();
        if (!owner.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            result.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "");
            if (result.Count > RelationKeyRules.MaxKeyColumns)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Collects relation definitions and reason-coded warnings with the SEC-REL151-02 limits.</summary>
    private sealed class RelationCollector
    {
        public const int MaxRelations = 10_000;

        private readonly List<DbtRelationshipDefinition> _relationships = [];
        private readonly List<string> _warnings = [];
        private int _attempts;
        private bool _overflow;

        public IReadOnlyList<DbtRelationshipDefinition> Relationships => _relationships;

        public IReadOnlyList<string> Finish()
        {
            if (_overflow)
            {
                _warnings.Add($"Relation import limited to {MaxRelations} relations per manifest; the rest were skipped: TOO_MANY_RELATIONS");
            }

            return _warnings;
        }

        public void Skip(string name, string child, string parent, string code)
        {
            if (!Count())
            {
                return;
            }

            AddWarning(name, child, parent, code);
        }

        public void TryAdd(string name, string parent, string child, IReadOnlyList<string> parentColumns, IReadOnlyList<string> childColumns)
        {
            if (!Count())
            {
                return;
            }

            if (!DbtIdentifierSanitizer.IsValidIdentifier(name) || !DbtIdentifierSanitizer.IsValidIdentifier(parent) ||
                !DbtIdentifierSanitizer.IsValidIdentifier(child) || HasInvalid(parentColumns) || HasInvalid(childColumns))
            {
                AddWarning(name, child, parent, "INVALID_IDENTIFIER");
                return;
            }

            var violation = RelationKeyRules.Validate(parentColumns, childColumns);
            if (violation is not null)
            {
                AddWarning(name, child, parent, violation.Value switch
                {
                    RelationKeyViolation.MissingColumns => "MISSING_COLUMNS",
                    RelationKeyViolation.CountMismatch => "COUNT_MISMATCH",
                    RelationKeyViolation.DuplicateColumn => "DUPLICATE_COLUMN",
                    RelationKeyViolation.TooWide => "TOO_WIDE",
                    _ => "INVALID_IDENTIFIER"
                });
                return;
            }

            _relationships.Add(new DbtRelationshipDefinition(name, parent, child, [.. parentColumns], [.. childColumns]));
        }

        private bool Count()
        {
            if (_attempts >= MaxRelations)
            {
                _overflow = true;
                return false;
            }

            _attempts++;
            return true;
        }

        private static bool HasInvalid(IReadOnlyList<string> columns)
        {
            foreach (var c in columns)
            {
                if (!DbtIdentifierSanitizer.IsValidIdentifier(c))
                {
                    return true;
                }
            }

            return false;
        }

        private void AddWarning(string name, string child, string parent, string code) =>
            _warnings.Add($"Relation '{DbtIdentifierSanitizer.ForLog(name)}' ({DbtIdentifierSanitizer.ForLog(child)} -> {DbtIdentifierSanitizer.ForLog(parent)}) skipped: {code}");
    }

    private static CatalogKeyDefinitionImport.Collector GetBuilder(Dictionary<string, CatalogKeyDefinitionImport.Collector> builders, string uniqueId)
    {
        if (!builders.TryGetValue(uniqueId, out var builder))
        {
            builder = new CatalogKeyDefinitionImport.Collector();
            builders[uniqueId] = builder;
        }

        return builder;
    }

    /// <summary>Reads a string array of at most the key limit; null (invalid) for any other shape. The length is checked before enumerating.</summary>
    private static List<string>? ReadColumnArray(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var length = array.GetArrayLength();
        if (length is 0 or > TableKeyDefinitionValidator.MaxColumnsPerKey)
        {
            return null;
        }

        var result = new List<string>(length);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                return null;
            }

            result.Add(item.GetString()!);
        }

        return result;
    }

    /// <summary>A model-level (<paramref name="columnName"/> null) or column-level dbt constraint of type primary_key or unique.</summary>
    private static void CollectKeyConstraint(CatalogKeyDefinitionImport.Collector builder, JsonElement constraint, string? columnName, List<string>? columnLevelPrimaryKey = null)
    {
        if (constraint.ValueKind != JsonValueKind.Object ||
            !constraint.TryGetProperty("type", out var typeProp) ||
            typeProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var type = typeProp.GetString();
        var isPrimaryKey = string.Equals(type, "primary_key", StringComparison.OrdinalIgnoreCase);
        var isUnique = string.Equals(type, "unique", StringComparison.OrdinalIgnoreCase);
        if (!isPrimaryKey && !isUnique)
        {
            return;
        }

        if (columnName is not null)
        {
            if (isPrimaryKey)
            {
                columnLevelPrimaryKey?.Add(columnName);
            }
            else
            {
                builder.AddUnique([columnName]);
            }

            return;
        }

        var columns = ReadColumnArray(constraint, "columns");
        if (isPrimaryKey)
        {
            builder.AddPrimaryKey(columns);
        }
        else
        {
            builder.AddUnique(columns);
        }
    }

    /// <summary>dbt <c>unique</c> and <c>unique_combination_of_columns</c> tests declare a unique constraint on the attached model.</summary>
    private static void CollectUniqueTest(JsonElement node, Dictionary<string, CatalogKeyDefinitionImport.Collector> builders)
    {
        if (!node.TryGetProperty("test_metadata", out var testMetadata) ||
            !testMetadata.TryGetProperty("name", out var nameProp) ||
            nameProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var testName = nameProp.GetString();
        var isUnique = string.Equals(testName, "unique", StringComparison.OrdinalIgnoreCase);
        var isCombination = string.Equals(testName, "unique_combination_of_columns", StringComparison.OrdinalIgnoreCase);
        if (!isUnique && !isCombination)
        {
            return;
        }

        string? attached = node.TryGetProperty("attached_node", out var attachedProp) && attachedProp.ValueKind == JsonValueKind.String
            ? attachedProp.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(attached) &&
            node.TryGetProperty("depends_on", out var depends) &&
            depends.TryGetProperty("nodes", out var depNodes) &&
            depNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in depNodes.EnumerateArray())
            {
                var depId = dep.ValueKind == JsonValueKind.String ? dep.GetString() : null;
                if (depId is not null && depId.StartsWith("model.", StringComparison.OrdinalIgnoreCase))
                {
                    attached = depId;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(attached) || !testMetadata.TryGetProperty("kwargs", out var kwargs) || kwargs.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var builder = GetBuilder(builders, attached!);
        if (isCombination)
        {
            builder.AddUnique(ReadColumnArray(kwargs, "combination_of_columns"));
        }
        else if (kwargs.TryGetProperty("column_name", out var columnProp) && columnProp.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrWhiteSpace(columnProp.GetString()))
        {
            builder.AddUnique([columnProp.GetString()!]);
        }
    }

    private static DbtDeclaredKeys? ToDeclaredKeys(CatalogKeyDefinitionImport.Collector collector) =>
        collector.Invalid ? new DbtDeclaredKeys(null, [], true)
        : !collector.HasDeclaration ? null
        : new DbtDeclaredKeys(collector.PrimaryKey, collector.Indexes!.Select(i => i.Columns).ToList(), false);

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

    private const int MaxSyntheticSampleKeysPerRow = 256;
    private const int MaxSyntheticSampleStringLength = 1024;

    /// <summary>
    /// Reads the <c>meta</c> object of a node and extracts inline synthetic samples against the node's columns.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>>? ExtractSyntheticSamplesFromNode(
        JsonElement node,
        IReadOnlyCollection<string> modelColumns) =>
        node.TryGetProperty("meta", out var metaProp) && metaProp.ValueKind == JsonValueKind.Object
            ? ExtractSyntheticSamples(metaProp, modelColumns)
            : null;

    /// <summary>
    /// Extracts inline synthetic sample rows from a dbt <c>meta</c> object (MCP-02). Only inline JSON arrays of
    /// objects are accepted; string values (including would-be file paths) are never interpreted, so the file system
    /// is never touched. Rows are capped, keys that are not model columns are dropped, and only scalar values of
    /// bounded size are kept.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, object?>>? ExtractSyntheticSamples(
        JsonElement metaProp,
        IReadOnlyCollection<string> modelColumns)
    {
        if (!metaProp.TryGetProperty("synthetic_samples", out var sampleElem) &&
            !metaProp.TryGetProperty("syntheticSamples", out sampleElem) &&
            !metaProp.TryGetProperty("sample_rows", out sampleElem) &&
            !metaProp.TryGetProperty("sampleRows", out sampleElem))
        {
            return null;
        }

        if (sampleElem.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var allowedColumns = new HashSet<string>(modelColumns, StringComparer.OrdinalIgnoreCase);
        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in sampleElem.EnumerateArray())
        {
            if (list.Count >= SyntheticSampleRowGenerator.MaxPredefinedSampleRows)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var keys = 0;
            foreach (var prop in item.EnumerateObject())
            {
                if (++keys > MaxSyntheticSampleKeysPerRow)
                {
                    break;
                }

                if (!allowedColumns.Contains(prop.Name))
                {
                    continue;
                }

                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        var text = prop.Value.GetString();
                        if (text is null || text.Length <= MaxSyntheticSampleStringLength)
                        {
                            dict[prop.Name] = text;
                        }
                        break;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                    case JsonValueKind.Null:
                        dict[prop.Name] = ConvertJsonElement(prop.Value);
                        break;
                }
            }

            if (dict.Count > 0)
            {
                list.Add(dict);
            }
        }

        return list.Count > 0 ? list : null;
    }

    private static object? ConvertJsonElement(JsonElement elem) =>
        elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.TryGetInt64(out var l) ? l : elem.TryGetDouble(out var d) ? d : elem.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => elem.GetRawText()
        };
}
