namespace Autheris.Application.DataCatalog.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class OpenApiIngestionService : IOpenApiIngestionService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ILogger<OpenApiIngestionService> _logger;

    public OpenApiIngestionService(
        ITableMetadataRepository metadataRepository,
        ILogger<OpenApiIngestionService> logger)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OpenApiIngestionResult> IngestOpenApiStreamAsync(
        Stream stream,
        string domain = "external",
        string? defaultBaseUrl = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return await IngestOpenApiJsonAsync(json, domain, defaultBaseUrl, ct).ConfigureAwait(false);
    }

    public async Task<OpenApiIngestionResult> IngestOpenApiJsonAsync(
        string openApiJson,
        string domain = "external",
        string? defaultBaseUrl = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openApiJson);

        using var doc = JsonDocument.Parse(openApiJson);
        var root = doc.RootElement;

        var title = "External OpenAPI Service";
        if (root.TryGetProperty("info", out var info) && info.TryGetProperty("title", out var titleProp))
        {
            title = titleProp.GetString() ?? title;
        }

        var serverUrl = defaultBaseUrl ?? "https://api.external.service";
        if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            var firstServer = servers.EnumerateArray().FirstOrDefault();
            if (firstServer.ValueKind == JsonValueKind.Object && firstServer.TryGetProperty("url", out var urlProp))
            {
                serverUrl = urlProp.GetString() ?? serverUrl;
            }
        }

        var warnings = new List<string>();
        var ingestedTableNames = new List<string>();
        var totalColumns = 0;

        // Path mapping cache
        var pathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in paths.EnumerateObject())
            {
                var pathStr = p.Name;
                var segments = pathStr.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0)
                {
                    var lastSegment = segments[^1];
                    pathMap[lastSegment] = pathStr;
                }
            }
        }

        if (root.TryGetProperty("components", out var components) &&
            components.TryGetProperty("schemas", out var schemas) &&
            schemas.ValueKind == JsonValueKind.Object)
        {
            foreach (var schemaProp in schemas.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();

                var schemaName = schemaProp.Name;
                var schemaObj = schemaProp.Value;

                var schemaDesc = schemaObj.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;
                var schemaLongDesc = schemaObj.TryGetProperty("x-long-description", out var longDescProp) ? longDescProp.GetString() : null;

                var columns = new List<TableColumn>();
                var primaryKeys = new List<string>();

                if (schemaObj.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in props.EnumerateObject())
                    {
                        var colName = prop.Name;
                        var colVal = prop.Value;

                        var typeStr = colVal.TryGetProperty("type", out var tp) ? tp.GetString() ?? "string" : "string";
                        var formatStr = colVal.TryGetProperty("format", out var fp) ? fp.GetString() : null;
                        var colDesc = colVal.TryGetProperty("description", out var cdp) ? cdp.GetString() : null;
                        var colLongDesc = colVal.TryGetProperty("x-long-description", out var cldp) ? cldp.GetString() : null;
                        var isSensitive = colVal.TryGetProperty("x-sensitive", out var xSens) && xSens.GetBoolean();

                        var effectiveType = !string.IsNullOrWhiteSpace(formatStr) ? formatStr : typeStr;

                        var metaDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (colVal.TryGetProperty("x-dbt-meta", out var xMeta) && xMeta.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var xm in xMeta.EnumerateObject())
                            {
                                metaDict[xm.Name] = JsonValueText.From(xm.Value);                            }
                        }

                        columns.Add(new TableColumn
                        {
                            ColumnName = colName,
                            DataType = effectiveType,
                            Description = colDesc,
                            LongDescription = colLongDesc,
                            DocumentationSource = "OpenApi",
                            IsSensitive = isSensitive,
                            Meta = metaDict
                        });

                        totalColumns++;
                    }
                }

                if (columns.Count == 0)
                {
                    warnings.Add($"Schema '{schemaName}' has no object properties. Skipped.");
                    continue;
                }

                // Primary Key Detection
                if (schemaObj.TryGetProperty("required", out var reqArray) && reqArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var req in reqArray.EnumerateArray())
                    {
                        var rName = req.GetString();
                        if (rName != null && columns.Any(c => string.Equals(c.ColumnName, rName, StringComparison.OrdinalIgnoreCase)))
                        {
                            primaryKeys.Add(rName);
                        }
                    }
                }

                if (primaryKeys.Count == 0)
                {
                    var idCol = columns.FirstOrDefault(c => string.Equals(c.ColumnName, "id", StringComparison.OrdinalIgnoreCase) ||
                                                           c.ColumnName.EndsWith("_id", StringComparison.OrdinalIgnoreCase));
                    if (idCol != null)
                    {
                        primaryKeys.Add(idCol.ColumnName);
                    }
                    else
                    {
                        primaryKeys.Add(columns[0].ColumnName);
                    }
                }

                // Path resolution
                var resolvedPath = pathMap.TryGetValue(schemaName, out var pMatch)
                    ? pMatch
                    : (pathMap.TryGetValue(schemaName + "s", out var plMatch) ? plMatch : $"/{schemaName.ToLowerInvariant()}");

                var tableName = schemaName.ToLowerInvariant();
                var tableId = new TableIdentifier(domain, "api", tableName);

                var tableMetadata = new TableMetadata
                {
                    Identifier = tableId,
                    Table = new Table
                    {
                        SchemaName = "api",
                        TableName = tableName,
                        DisplayName = schemaName,
                        Description = schemaDesc,
                        LongDescription = schemaLongDesc,
                        DocumentationSource = "OpenApi",
                        DataSourceType = DataSourceType.HttpDeclarative,
                        HttpEndpoint = new HttpEndpointDescriptor
                        {
                            BaseUrl = serverUrl,
                            PathTemplate = resolvedPath,
                            Method = "GET",
                            AuthMode = HttpAuthMode.None
                        }
                    },
                    Columns = columns,
                    PrimaryKeyColumns = primaryKeys
                };

                // SEC M-30: an OpenAPI (re-)ingestion must never weaken governance of an existing table
                // (RequiresFourEyes, Sensitivity, IsActive, column IsSensitive, masking rules) nor re-route it
                // (DataSourceType / HttpEndpoint of existing tables are preserved).
                var existing = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
                if (existing != null)
                {
                    tableMetadata = CatalogGovernanceRatchet.Merge(tableMetadata, existing);
                    if (existing.Table.HttpEndpoint != null &&
                        !string.Equals(existing.Table.HttpEndpoint.BaseUrl, serverUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"Schema '{schemaName}': existing endpoint of table '{tableId}' was kept; endpoint changes require an administrative update.");
                    }
                }

                await _metadataRepository.UpsertTableMetadataAsync(tableMetadata, ct).ConfigureAwait(false);
                ingestedTableNames.Add(tableName);
            }
        }
        else
        {
            warnings.Add("OpenAPI specification has no 'components.schemas' definitions.");
        }

        _logger.LogInformation("Ingested {Count} tables and {Cols} columns from OpenAPI spec '{Title}'.",
            ingestedTableNames.Count, totalColumns, title);

        return new OpenApiIngestionResult(
            Success: true,
            ServiceTitle: title,
            IngestedTablesCount: ingestedTableNames.Count,
            IngestedColumnsCount: totalColumns,
            IngestedTableNames: ingestedTableNames,
            Warnings: warnings
        );
    }
}
