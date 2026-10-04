namespace Autheris.Extensions.Backstage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.SchemaRegistry;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Application.Integrations.Backstage;

public sealed class BackstageCatalogExportService : IBackstageCatalogExportService
{
    private readonly ISchemaRegistryService? _schemaRegistry;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly ISchemaSunsettingService? _sunsettingService;
    private readonly GatewayOptions _options;
    private readonly ILogger<BackstageCatalogExportService>? _logger;

    public BackstageCatalogExportService(
        IOptions<GatewayOptions>? options = null,
        ISchemaRegistryService? schemaRegistry = null,
        ITableMetadataRepository? tableRepository = null,
        ISchemaSunsettingService? sunsettingService = null,
        ILogger<BackstageCatalogExportService>? logger = null)
    {
        _options = options?.Value ?? new GatewayOptions();
        _schemaRegistry = schemaRegistry;
        _tableRepository = tableRepository;
        _sunsettingService = sunsettingService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BackstageEntity>> ExportCatalogEntitiesAsync(
        string? kindFilter = null,
        string? typeFilter = null,
        CancellationToken cancellationToken = default)
    {
        var entities = new List<BackstageEntity>();
        var backstageOpts = _options.Backstage;
        var baseUrl = backstageOpts.BaseUrl.TrimEnd('/');
        var endpointPath = _options.GraphQL.EndpointPath.StartsWith('/')
            ? _options.GraphQL.EndpointPath
            : "/" + _options.GraphQL.EndpointPath;

        // 1. Core Federated GraphQL Gateway Entity
        var federatedEntity = new BackstageEntity
        {
            ApiVersion = "backstage.io/v1alpha1",
            Kind = "API",
            Metadata = new BackstageMetadata
            {
                Name = "autheris-federated",
                Namespace = backstageOpts.DefaultNamespace,
                Title = "Enterprise Federated GraphQL Gateway",
                Description = "Unified zero-trust GraphQL federation gateway with real-time Casbin ABAC, column masking, and row-level security.",
                Tags = ["graphql", "federated", "zero-trust", "gateway"],
                Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["backstage.io/managed-by-location"] = "url:/api/integrations/backstage/catalog-entities/autheris-federated",
                    ["autheris.io/type"] = "federated-gateway",
                    ["autheris.io/endpoint"] = endpointPath,
                    ["autheris.io/governance-mode"] = "strict-zero-trust"
                },
                Links =
                [
                    new BackstageEntityLink
                    {
                        Url = string.IsNullOrEmpty(baseUrl) ? endpointPath : $"{baseUrl}{endpointPath}",
                        Title = "GraphQL Query Endpoint",
                        Icon = "web"
                    },
                    new BackstageEntityLink
                    {
                        Url = string.IsNullOrEmpty(baseUrl) ? "/health/ready" : $"{baseUrl}/health/ready",
                        Title = "Gateway Health",
                        Icon = "help"
                    }
                ]
            },
            Spec = new BackstageSpec
            {
                Type = "graphql",
                Lifecycle = "production",
                Owner = backstageOpts.DefaultOwner,
                System = backstageOpts.DefaultSystem,
                Definition = "type Query { ping: String }\n"
            }
        };
        entities.Add(federatedEntity);

        // 2. Core Dynamic OpenAPI 3.1 REST API Entity
        var openApiEntity = new BackstageEntity
        {
            ApiVersion = "backstage.io/v1alpha1",
            Kind = "API",
            Metadata = new BackstageMetadata
            {
                Name = "autheris-openapi",
                Namespace = backstageOpts.DefaultNamespace,
                Title = "Enterprise Dynamic OpenAPI 3.1 REST API",
                Description = "Dynamic REST & OpenAPI 3.1 projection of governed enterprise entities with live Casbin policy enforcement.",
                Tags = ["rest", "openapi", "zero-trust", "swagger"],
                Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["backstage.io/managed-by-location"] = "url:/api/integrations/backstage/catalog-entities/autheris-openapi",
                    ["autheris.io/endpoint"] = "/api/openapi/v3.json",
                    ["autheris.io/swagger-ui"] = "/swagger"
                },
                Links =
                [
                    new BackstageEntityLink
                    {
                        Url = string.IsNullOrEmpty(baseUrl) ? "/swagger" : $"{baseUrl}/swagger",
                        Title = "Swagger UI",
                        Icon = "web"
                    },
                    new BackstageEntityLink
                    {
                        Url = string.IsNullOrEmpty(baseUrl) ? "/api/openapi/v3.json" : $"{baseUrl}/api/openapi/v3.json",
                        Title = "OpenAPI Specification",
                        Icon = "code"
                    }
                ]
            },
            Spec = new BackstageSpec
            {
                Type = "openapi",
                Lifecycle = "production",
                Owner = backstageOpts.DefaultOwner,
                System = backstageOpts.DefaultSystem,
                Definition = string.IsNullOrEmpty(baseUrl) ? "/api/openapi/v3.json" : $"{baseUrl}/api/openapi/v3.json"
            }
        };
        entities.Add(openApiEntity);

        // 3. Subgraphs from Schema Registry (P8)
        if (_schemaRegistry != null)
        {
            try
            {
                var services = await _schemaRegistry.GetAllServicesAsync(cancellationToken);
                foreach (var service in services)
                {
                    var latest = await _schemaRegistry.GetLatestSchemaAsync(service, cancellationToken);
                    if (latest == null) continue;

                    var entityName = SanitizeEntityName($"subgraph-{latest.ServiceName}");
                    var annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["backstage.io/managed-by-location"] = $"url:/api/integrations/backstage/catalog-entities/{entityName}",
                        ["autheris.io/subgraph"] = latest.ServiceName,
                        ["autheris.io/version"] = latest.Version,
                        ["autheris.io/registered-at"] = latest.RegisteredAt.ToString("O")
                    };

                    if (!string.IsNullOrWhiteSpace(latest.GitCommit))
                        annotations["autheris.io/git-commit"] = latest.GitCommit;
                    if (!string.IsNullOrWhiteSpace(latest.GitBranch))
                        annotations["autheris.io/git-branch"] = latest.GitBranch;
                    if (!string.IsNullOrWhiteSpace(latest.RegisteredBy))
                        annotations["autheris.io/registered-by"] = latest.RegisteredBy;

                    var subgraphEntity = new BackstageEntity
                    {
                        ApiVersion = "backstage.io/v1alpha1",
                        Kind = "API",
                        Metadata = new BackstageMetadata
                        {
                            Name = entityName,
                            Namespace = backstageOpts.DefaultNamespace,
                            Title = $"{latest.ServiceName} Subgraph API",
                            Description = $"Federated Subgraph '{latest.ServiceName}' registered in Autheris Schema Registry (v{latest.Version}).",
                            Tags = ["graphql", "subgraph", "federation", SanitizeEntityName(latest.ServiceName)],
                            Annotations = annotations
                        },
                        Spec = new BackstageSpec
                        {
                            Type = "graphql",
                            Lifecycle = latest.IsActive ? "production" : "deprecated",
                            Owner = !string.IsNullOrWhiteSpace(latest.RegisteredBy)
                                ? $"user:{latest.RegisteredBy}"
                                : backstageOpts.DefaultOwner,
                            System = backstageOpts.DefaultSystem,
                            Definition = latest.Sdl
                        }
                    };
                    entities.Add(subgraphEntity);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load subgraphs from Schema Registry for Backstage export.");
            }
        }

        // 4. Governed Tables / Data Products
        if (backstageOpts.IncludeTablesAsApis && _tableRepository != null)
        {
            try
            {
                var tables = await _tableRepository.GetAllTablesAsync(cancellationToken);
                IReadOnlyList<FieldSunsettingRule>? sunsettingRules = null;

                if (_sunsettingService != null)
                {
                    try
                    {
                        sunsettingRules = await _sunsettingService.GetRulesAsync(cancellationToken);
                    }
                    catch
                    {
                        // Ignore sunsetting service issues
                    }
                }

                foreach (var tableMetadata in tables)
                {
                    var table = tableMetadata.Table;
                    var entityName = SanitizeEntityName($"data-{table.SchemaName}-{table.TableName}");
                    var tags = new List<string> { "data-product", "table", SanitizeEntityName(table.SourceType) };

                    if (table.RequiresFourEyes || table.IsHighlySensitive)
                    {
                        tags.Add("four-eyes-required");
                        tags.Add("pii");
                    }

                    if (!string.IsNullOrWhiteSpace(table.DocumentationSource))
                    {
                        tags.Add(SanitizeEntityName(table.DocumentationSource));
                    }

                    if (!string.IsNullOrWhiteSpace(table.Sensitivity))
                    {
                        tags.Add(SanitizeEntityName($"sensitivity-{table.Sensitivity}"));
                    }

                    var annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["backstage.io/managed-by-location"] = $"url:/api/integrations/backstage/catalog-entities/{entityName}",
                        ["autheris.io/source-type"] = table.SourceType,
                        ["autheris.io/schema-name"] = table.SchemaName,
                        ["autheris.io/table-name"] = table.TableName,
                        ["autheris.io/sensitivity"] = table.Sensitivity,
                        ["autheris.io/requires-four-eyes"] = table.RequiresFourEyes ? "true" : "false"
                    };

                    if (!string.IsNullOrWhiteSpace(table.DocumentationSource))
                    {
                        annotations["autheris.io/documentation-source"] = table.DocumentationSource;
                    }

                    // Check if table has active sunsetting rules
                    var isDeprecated = false;
                    if (sunsettingRules != null)
                    {
                        var matchingRule = sunsettingRules.FirstOrDefault(r =>
                            string.Equals(r.TargetTable, table.TableName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(r.TargetTable, $"{table.SchemaName}.{table.TableName}", StringComparison.OrdinalIgnoreCase));

                        if (matchingRule != null)
                        {
                            annotations["autheris.io/sunsetting-rule-id"] = matchingRule.Id.ToString();
                            annotations["autheris.io/sunsetting-deprecated-at"] = matchingRule.DeprecatedAt.ToString("O");
                            annotations["autheris.io/sunsetting-sunset-at"] = matchingRule.SunsetAt.ToString("O");
                            if (!string.IsNullOrWhiteSpace(matchingRule.ReplacementField))
                            {
                                annotations["autheris.io/sunsetting-replacement"] = matchingRule.ReplacementField;
                            }

                            if (DateTimeOffset.UtcNow >= matchingRule.SunsetAt)
                            {
                                isDeprecated = true;
                            }
                        }
                    }

                    var tableEntity = new BackstageEntity
                    {
                        ApiVersion = "backstage.io/v1alpha1",
                        Kind = "API",
                        Metadata = new BackstageMetadata
                        {
                            Name = entityName,
                            Namespace = backstageOpts.DefaultNamespace,
                            Title = !string.IsNullOrWhiteSpace(table.DisplayName) ? table.DisplayName : $"{table.SchemaName}.{table.TableName}",
                            Description = table.Description ?? table.LongDescription ?? $"Governed enterprise data entity '{table.SchemaName}.{table.TableName}'.",
                            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                            Annotations = annotations
                        },
                        Spec = new BackstageSpec
                        {
                            Type = "openapi",
                            Lifecycle = isDeprecated || !table.IsActive ? "deprecated" : "production",
                            Owner = backstageOpts.DefaultOwner,
                            System = backstageOpts.DefaultSystem
                        }
                    };

                    entities.Add(tableEntity);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load tables for Backstage export.");
            }
        }

        // Apply filters
        var result = entities.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(kindFilter))
        {
            result = result.Where(e => string.Equals(e.Kind, kindFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(typeFilter))
        {
            result = result.Where(e => string.Equals(e.Spec.Type, typeFilter, StringComparison.OrdinalIgnoreCase));
        }

        return result.ToList();
    }

    public async Task<BackstageEntity?> ExportEntityByNameAsync(
        string entityName,
        CancellationToken cancellationToken = default)
    {
        var sanitized = SanitizeEntityName(entityName);
        var all = await ExportCatalogEntitiesAsync(cancellationToken: cancellationToken);
        return all.FirstOrDefault(e => string.Equals(e.Metadata.Name, sanitized, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(e.Metadata.Name, entityName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> ExportCatalogEntitiesYamlAsync(
        string? kindFilter = null,
        string? typeFilter = null,
        CancellationToken cancellationToken = default)
    {
        var entities = await ExportCatalogEntitiesAsync(kindFilter, typeFilter, cancellationToken);
        var sb = new StringBuilder();

        foreach (var entity in entities)
        {
            sb.AppendLine("---");
            var yaml = BackstageYamlSerializer.Serialize(entity);
            sb.AppendLine(yaml.TrimEnd());
        }

        return sb.ToString();
    }

    public static string SanitizeEntityName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unnamed";
        var cleaned = Regex.Replace(raw.Trim().ToLowerInvariant(), @"[^a-z0-9_.-]", "-");
        cleaned = Regex.Replace(cleaned, @"-+", "-").Trim('-');
        if (cleaned.Length > 63) cleaned = cleaned[..63].TrimEnd('-');
        return string.IsNullOrEmpty(cleaned) ? "entity" : cleaned;
    }
}
