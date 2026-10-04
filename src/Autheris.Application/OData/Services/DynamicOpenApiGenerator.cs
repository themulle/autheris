namespace Autheris.Application.OData.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.OData.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Dynamic OpenAPI 3.1 specification generator engine for OData v4 endpoints (F-API-03).
/// </summary>
public sealed class DynamicOpenApiGenerator : IDynamicOpenApiGenerator
{
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly ILogger<DynamicOpenApiGenerator> _logger;
    private readonly OpenApiDocumentOptions _options;

    public DynamicOpenApiGenerator(
        ITableMetadataRepository metadataRepo,
        ILogger<DynamicOpenApiGenerator> logger,
        OpenApiDocumentOptions? options = null)
    {
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new OpenApiDocumentOptions();
    }

    public async Task<string> GenerateOpenApiJsonAsync(string? domainScope = null, bool modular = false, CancellationToken ct = default)
    {
        var rootNode = await BuildOpenApiNodeAsync(domainScope, modular, ct).ConfigureAwait(false);
        return rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> GenerateOpenApiYamlAsync(string? domainScope = null, bool modular = false, CancellationToken ct = default)
    {
        var rootNode = await BuildOpenApiNodeAsync(domainScope, modular, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        ConvertJsonNodeToYaml(rootNode, sb, indentLevel: 0);
        return sb.ToString();
    }

    public async Task<string?> GenerateEntitySchemaJsonAsync(TableIdentifier tableId, CancellationToken ct = default)
    {
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var table = allTables.FirstOrDefault(t =>
            string.Equals(t.Identifier.Domain, tableId.Domain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Identifier.Schema, tableId.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Identifier.TableName, tableId.TableName, StringComparison.OrdinalIgnoreCase));

        if (table == null)
        {
            return null;
        }

        var schemaNode = BuildEntitySchemaNode(table);
        schemaNode["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schemaNode["title"] = FormatSchemaName(table.Identifier);
        return schemaNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<OpenApiIndexDocument> GetIndexDocumentAsync(string? baseUrl = null, CancellationToken ct = default)
    {
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var serverPrefix = _options.ServerUrl.Trim('/');
        var root = string.IsNullOrWhiteSpace(baseUrl)
            ? (string.IsNullOrEmpty(serverPrefix) ? "" : "/" + serverPrefix)
            : (string.IsNullOrEmpty(serverPrefix) ? baseUrl.TrimEnd('/') : $"{baseUrl.TrimEnd('/')}/{serverPrefix}");

        var domainGroups = allTables
            .GroupBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .ToList();

        var summaries = new List<OpenApiDomainSummary>();
        var apis = new List<OpenApiApiEntry>
        {
            new("All Domains (Monolithic)", $"{root}/$openapi"),
            new("All Domains (Modular $ref)", $"{root}/$openapi?mode=modular"),
            new("Declarative SQL Endpoints", "/api/v1/queries/openapi.json")
        };

        foreach (var group in domainGroups)
        {
            var domain = group.Key;
            var tableNames = group.Select(t => t.Identifier.TableName).OrderBy(n => n).ToList();
            var jsonUrl = $"{root}/{domain}/openapi.json";
            var yamlUrl = $"{root}/{domain}/openapi.yaml";

            summaries.Add(new OpenApiDomainSummary(
                Domain: domain,
                TableCount: group.Count(),
                JsonUrl: jsonUrl,
                YamlUrl: yamlUrl,
                Tables: tableNames
            ));

            var displayName = char.ToUpperInvariant(domain[0]) + (domain.Length > 1 ? domain[1..] : "");
            apis.Add(new OpenApiApiEntry(
                Name: $"{displayName} Domain ({group.Count()} tables)",
                Url: jsonUrl,
                Domain: domain
            ));
        }

        return new OpenApiIndexDocument(
            TotalDomains: domainGroups.Count,
            TotalTables: allTables.Count,
            Domains: summaries,
            Apis: apis
        );
    }

    private async Task<JsonObject> BuildOpenApiNodeAsync(string? domainScope, bool modular, CancellationToken ct)
    {
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);

        IEnumerable<TableMetadata> tables = allTables;
        if (!string.IsNullOrWhiteSpace(domainScope))
        {
            tables = tables.Where(t => string.Equals(t.Identifier.Domain, domainScope, StringComparison.OrdinalIgnoreCase));
        }

        var tableList = tables.OrderBy(t => t.Identifier.Domain).ThenBy(t => t.Identifier.TableName).ToList();

        var root = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = !string.IsNullOrWhiteSpace(domainScope) ? $"{_options.Title} - {domainScope.ToUpperInvariant()}" : _options.Title,
                ["version"] = _options.Version,
                ["description"] = _options.Description
            },
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = _options.ServerUrl,
                    ["description"] = "Autheris OData v4 Service Root"
                }
            }
        };

        var paths = new JsonObject();
        var schemas = new JsonObject();

        foreach (var table in tableList)
        {
            var domain = table.Identifier.Domain;
            var schema = table.Identifier.Schema;
            var tableName = table.Identifier.TableName;
            var pathKey = $"/{domain}/{schema}/{tableName}";
            var schemaName = FormatSchemaName(table.Identifier);
            var remoteRefUrl = $"{_options.ServerUrl.TrimEnd('/')}/$openapi/schemas/{domain}/{schema}/{tableName}";

            if (modular)
            {
                schemas[schemaName] = new JsonObject
                {
                    ["$ref"] = remoteRefUrl
                };
            }
            else
            {
                schemas[schemaName] = BuildEntitySchemaNode(table);
            }

            // 2. Build Path Operation
            var getOperation = new JsonObject
            {
                ["summary"] = $"Query {domain}.{tableName}",
                ["description"] = $"OData v4 entity set query endpoint for table '{domain}.{schema}.{tableName}'. Supports $select, $filter, $top, $skip, $count.",
                ["tags"] = new JsonArray { domain }
            };

            var parameters = new JsonArray
            {
                CreateQueryParam("$select", "string", "Comma-separated list of properties to select (e.g. id,amount,customer)."),
                CreateQueryParam("$filter", "string", "OData filter expression (e.g. amount gt 1000 and status eq 'Active')."),
                CreateQueryParam("$top", "integer", "Maximum number of records to return (max 1000)."),
                CreateQueryParam("$skip", "integer", "Number of records to skip for pagination."),
                CreateQueryParam("$count", "boolean", "Whether to include the total inline record count (@odata.count).")
            };
            getOperation["parameters"] = parameters;

            // 3. Responses
            var itemRef = modular ? remoteRefUrl : $"#/components/schemas/{schemaName}";
            var responses = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = "Successful OData v4 query response.",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["@odata.context"] = new JsonObject { ["type"] = "string" },
                                    ["@odata.count"] = new JsonObject { ["type"] = "integer" },
                                    ["value"] = new JsonObject
                                    {
                                        ["type"] = "array",
                                        ["items"] = new JsonObject
                                        {
                                            ["$ref"] = itemRef
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                ["400"] = new JsonObject
                {
                    ["description"] = "Invalid OData query option syntax or parameter validation error."
                },
                ["401"] = new JsonObject
                {
                    ["description"] = "Unauthorized. Missing or invalid authentication token."
                },
                ["403"] = new JsonObject
                {
                    ["description"] = "Forbidden. Zero-Trust policy or row/column consent denied."
                }
            };

            getOperation["responses"] = responses;

            paths[pathKey] = new JsonObject
            {
                ["get"] = getOperation
            };
        }

        root["paths"] = paths;
        root["components"] = new JsonObject
        {
            ["schemas"] = schemas
        };

        return root;
    }

    private JsonObject BuildEntitySchemaNode(TableMetadata table)
    {
        var domain = table.Identifier.Domain;
        var schema = table.Identifier.Schema;
        var tableName = table.Identifier.TableName;

        var entityDesc = !string.IsNullOrWhiteSpace(table.Table.Description)
            ? table.Table.Description
            : (!string.IsNullOrWhiteSpace(table.Table.DisplayName)
                ? table.Table.DisplayName
                : $"Entity model for {domain}.{schema}.{tableName}");

        var entitySchema = new JsonObject
        {
            ["type"] = "object",
            ["description"] = entityDesc
        };

        if (!string.IsNullOrWhiteSpace(table.Table.LongDescription))
        {
            entitySchema["x-long-description"] = table.Table.LongDescription;
        }

        var properties = new JsonObject();
        var requiredCols = new JsonArray();

        foreach (var col in table.Columns)
        {
            var colProp = MapColumnToJsonSchema(col);
            properties[col.ColumnName] = colProp;

            if (table.PrimaryKeyColumns.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase))
            {
                requiredCols.Add(col.ColumnName);
            }
        }

        entitySchema["properties"] = properties;
        if (requiredCols.Count > 0)
        {
            entitySchema["required"] = requiredCols;
        }

        return entitySchema;
    }

    private static JsonObject CreateQueryParam(string name, string type, string description)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["in"] = "query",
            ["required"] = false,
            ["description"] = description,
            ["schema"] = new JsonObject
            {
                ["type"] = type
            }
        };
    }

    private static JsonObject MapColumnToJsonSchema(TableColumn col)
    {
        var node = new JsonObject();
        var dt = col.DataType.Trim().ToLowerInvariant();

        switch (dt)
        {
            case "int":
            case "integer":
            case "smallint":
            case "tinyint":
                node["type"] = "integer";
                node["format"] = "int32";
                break;
            case "bigint":
                node["type"] = "integer";
                node["format"] = "int64";
                break;
            case "decimal":
            case "numeric":
            case "money":
            case "float":
            case "double":
            case "real":
                node["type"] = "number";
                node["format"] = "double";
                break;
            case "bool":
            case "boolean":
            case "bit":
                node["type"] = "boolean";
                break;
            case "date":
                node["type"] = "string";
                node["format"] = "date";
                break;
            case "timestamp":
            case "datetime":
            case "datetime2":
            case "timestamptz":
                node["type"] = "string";
                node["format"] = "date-time";
                break;
            case "uuid":
            case "guid":
                node["type"] = "string";
                node["format"] = "uuid";
                break;
            default:
                node["type"] = "string";
                break;
        }

        if (!string.IsNullOrWhiteSpace(col.Description))
        {
            node["description"] = col.Description;
        }

        if (!string.IsNullOrWhiteSpace(col.LongDescription))
        {
            node["x-long-description"] = col.LongDescription;
        }

        if (col.Meta != null && col.Meta.Count > 0)
        {
            var metaObj = new JsonObject();
            foreach (var kvp in col.Meta)
            {
                metaObj[kvp.Key] = JsonValue.Create(kvp.Value);
            }
            node["x-dbt-meta"] = metaObj;
        }

        if (col.IsSensitive)
        {
            node["x-sensitive"] = true;
        }

        return node;
    }

    private static string FormatSchemaName(TableIdentifier id)
    {
        return $"{id.Domain}_{id.Schema}_{id.TableName}";
    }

    private static void ConvertJsonNodeToYaml(JsonNode? node, StringBuilder sb, int indentLevel)
    {
        if (node == null)
        {
            sb.AppendLine("null");
            return;
        }

        var indent = new string(' ', indentLevel * 2);

        if (node is JsonObject obj)
        {
            if (obj.Count == 0)
            {
                sb.AppendLine("{}");
                return;
            }

            sb.AppendLine();
            foreach (var kvp in obj)
            {
                sb.Append(indent).Append(kvp.Key).Append(':');
                if (kvp.Value is JsonValue)
                {
                    sb.Append(' ');
                    ConvertJsonNodeToYaml(kvp.Value, sb, 0);
                }
                else
                {
                    ConvertJsonNodeToYaml(kvp.Value, sb, indentLevel + 1);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            if (arr.Count == 0)
            {
                sb.AppendLine("[]");
                return;
            }

            sb.AppendLine();
            foreach (var item in arr)
            {
                sb.Append(indent).Append("- ");
                if (item is JsonValue)
                {
                    ConvertJsonNodeToYaml(item, sb, 0);
                }
                else
                {
                    ConvertJsonNodeToYaml(item, sb, indentLevel + 1);
                }
            }
        }
        else if (node is JsonValue val)
        {
            var raw = val.ToJsonString();
            sb.AppendLine(raw);
        }
    }
}
