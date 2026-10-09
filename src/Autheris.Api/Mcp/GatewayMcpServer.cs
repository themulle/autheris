namespace Autheris.Api.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// MCP server on the official SDK. The SDK owns protocol and transport (Streamable HTTP, current protocol revisions,
/// stateless: every request runs with the identity of its own HTTP request); tools, resources and all governance
/// (guardrails, ABAC, four-eyes, PII scrubbing, token budget, audit) stay in the gateway services.
/// </summary>
public static class GatewayMcpServer
{
    public const string ServerName = "Autheris.McpServer";
    public const string DatasetResourceTemplate = "autheris://datasets/{dataset}";
    public const string CatalogSchemaResourceTemplate = "autheris://catalog/datasets/{datasetId}/schema";
    private const string DatasetResourcePrefix = "autheris://datasets/";
    private const string CatalogDatasetPrefix = "autheris://catalog/datasets/";

    private static readonly JsonElement EmptyObjectSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    public static IServiceCollection AddGatewayMcpServer(this IServiceCollection services, GatewayOptions options)
    {
        if (!options.Mcp.Enabled)
        {
            return services;
        }

        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = ServerName, Version = "2.0.0" };
                o.ServerInstructions = McpDatasetTools.ServerInstructions;
            })
            .WithHttpTransport(t => t.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync)
            .WithListResourcesHandler(ListResourcesAsync)
            .WithReadResourceHandler(ReadResourceAsync)
            .WithListResourceTemplatesHandler(ListResourceTemplatesAsync)
            .WithListPromptsHandler(ListPromptsAsync)
            .WithGetPromptHandler(GetPromptAsync);

        return services;
    }

    internal static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken ct)
    {
        var registry = context.Services!.GetRequiredService<IMcpToolRegistry>();
        var tools = registry.GetAvailableTools()
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = Schema(t.InputJsonSchema) })
            .ToList();
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    internal static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var request = context.Params ?? throw new McpProtocolException("Missing tool call parameters.", McpErrorCode.InvalidParams);
        var argumentsJson = request.Arguments == null ? "{}" : JsonSerializer.Serialize(request.Arguments);
        return await CallGuardedToolAsync(context.Services!, request.Name, argumentsJson, ct).ConfigureAwait(false);
    }

    internal static async ValueTask<ListResourcesResult> ListResourcesAsync(RequestContext<ListResourcesRequestParams> context, CancellationToken ct)
    {
        var resources = new List<Resource>
        {
            new() { Uri = "autheris://catalog/summary", Name = "Catalog Summary", Description = "Summary of visible enterprise catalog datasets", MimeType = "text/markdown" },
            new() { Uri = "autheris://catalog/datasources", Name = "Datasources", Description = "Status of connected enterprise data sources", MimeType = "text/markdown" },
            new() { Uri = "autheris://governance/my-access", Name = "My Access", Description = "Effective access profile, roles and policies of current caller", MimeType = "text/markdown" },
            new() { Uri = "autheris://api/openapi.json", Name = "OpenAPI Specification", Description = "OpenAPI 3.1 specification for Autheris REST endpoints", MimeType = "application/json" },
            new() { Uri = "autheris://api/docs/endpoints", Name = "API Endpoints Documentation", Description = "Markdown documentation of available REST endpoints", MimeType = "text/markdown" },
            new() { Uri = "autheris://api/docs/mcp-tools", Name = "MCP Tools Reference", Description = "Markdown reference for available MCP tools", MimeType = "text/markdown" }
        };

        var compiler = context.Services!.GetService<ISemanticMcpCompiler>();
        if (compiler != null)
        {
            var principal = McpProtocolHandler.BuildPrincipalFromSession(Caller(context.Services!));
            var semanticResources = await compiler.GetSemanticResourcesAsync(null, principal, ct).ConfigureAwait(false);
            resources.AddRange(semanticResources.Select(r => new Resource { Uri = r.Uri, Name = r.Name, Description = r.Description, MimeType = r.MimeType }));
        }

        return new ListResourcesResult { Resources = resources };
    }

    internal static ValueTask<ListResourceTemplatesResult> ListResourceTemplatesAsync(RequestContext<ListResourceTemplatesRequestParams> context, CancellationToken ct) =>
        ValueTask.FromResult(new ListResourceTemplatesResult
        {
            ResourceTemplates =
            [
                new ResourceTemplate
                {
                    Name = "dataset",
                    Title = "Dataset description",
                    UriTemplate = DatasetResourceTemplate,
                    Description = "The description of a dataset as returned by describe_dataset: columns, types, GraphQL field and example query. {dataset} is a dataset id from list_datasets (domain.schema.table).",
                    MimeType = "application/json"
                },
                new ResourceTemplate
                {
                    Name = "dataset_schema",
                    Title = "Catalog Dataset Schema",
                    UriTemplate = CatalogSchemaResourceTemplate,
                    Description = "Detailed schema and masking configuration for dataset {datasetId} (domain.schema.table).",
                    MimeType = "text/markdown"
                }
            ]
        });

    internal static async ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> context, CancellationToken ct)
    {
        var uri = context.Params?.Uri ?? throw new McpProtocolException("Missing resource uri.", McpErrorCode.InvalidParams);

        if (IsNativeResource(uri))
        {
            return await ReadNativeResourceAsync(uri, context.Services!, ct).ConfigureAwait(false);
        }

        if (uri.StartsWith(DatasetResourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Same path as the describe_dataset tool, so guardrails, ABAC and audit apply to resource reads as well.
            var dataset = Uri.UnescapeDataString(uri[DatasetResourcePrefix.Length..]);
            var result = await CallGuardedToolAsync(
                context.Services!,
                McpDatasetTools.DescribeDataset,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["dataset"] = dataset }),
                ct).ConfigureAwait(false);
            if (result.IsError == true)
            {
                throw new McpProtocolException($"Resource '{uri}' not found.", McpErrorCode.ResourceNotFound);
            }

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = text }] };
        }

        var compiler = context.Services!.GetService<ISemanticMcpCompiler>()
            ?? throw new McpProtocolException($"Resource '{uri}' not found.", McpErrorCode.ResourceNotFound);
        var principal = McpProtocolHandler.BuildPrincipalFromSession(Caller(context.Services!));
        var resources = await compiler.GetSemanticResourcesAsync(null, principal, ct).ConfigureAwait(false);
        var resource = resources.FirstOrDefault(r => string.Equals(r.Uri, uri, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpProtocolException($"Resource '{uri}' not found.", McpErrorCode.ResourceNotFound);
        return new ReadResourceResult { Contents = [new TextResourceContents { Uri = resource.Uri, MimeType = resource.MimeType, Text = resource.Text }] };
    }

    internal static ValueTask<ListPromptsResult> ListPromptsAsync(RequestContext<ListPromptsRequestParams> context, CancellationToken ct) =>
        ValueTask.FromResult(new ListPromptsResult { Prompts = GetAvailablePrompts().ToList() });

    internal static ValueTask<GetPromptResult> GetPromptAsync(RequestContext<GetPromptRequestParams> context, CancellationToken ct)
    {
        var request = context.Params ?? throw new McpProtocolException("Missing get prompt parameters.", McpErrorCode.InvalidParams);
        var stringArgs = request.Arguments?.ToDictionary(
            k => k.Key,
            v => v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var result = BuildPrompt(request.Name, stringArgs);
        return ValueTask.FromResult(result);
    }

    public static bool IsNativeResource(string uri) =>
        uri.StartsWith("autheris://catalog/", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("autheris://governance/", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("autheris://api/", StringComparison.OrdinalIgnoreCase);

    public static async Task<ReadResourceResult> ReadNativeResourceAsync(string uri, IServiceProvider services, CancellationToken ct = default)
    {
        var discovery = services.GetService<ICatalogDiscoveryService>();
        var dispatcher = services.GetService<IApiDispatcherService>();
        var session = Caller(services);
        var roles = session.Roles ?? [];
        bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
        bool isClusterAdmin = roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase);
        var requestContext = RequestContext.FromCaller(
            new CallerSecurityContext(
                UserSid: new Sid(session.UserSid ?? session.ServicePrincipalId),
                GroupSids: (session.GroupSids ?? []).Select(g => new Sid(g)).ToArray(),
                Roles: roles,
                Tenant: new TenantId(session.TenantId),
                IsGovernanceAdmin: isGovAdmin,
                IsClusterAdmin: isClusterAdmin),
            session.SessionId);

        // 1. autheris://catalog/summary
        if (string.Equals(uri, "autheris://catalog/summary", StringComparison.OrdinalIgnoreCase))
        {
            var datasets = discovery != null ? await discovery.ListDatasetsAsync(requestContext, ct).ConfigureAwait(false) : [];
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Autheris Data Catalog Summary\n");
            sb.AppendLine("| Dataset | Domain | Schema | Table | Type | Sensitivity | Description |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var d in datasets)
            {
                sb.AppendLine($"| `{d.DatasetId}` | {d.Domain} | {d.Schema} | {d.Table} | {d.SourceType} | {d.Sensitivity} | {d.Description ?? "-"} |");
            }
            return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = sb.ToString() }] };
        }

        // 2. autheris://catalog/datasets/{datasetId}/schema
        const string schemaPrefix = "autheris://catalog/datasets/";
        const string schemaSuffix = "/schema";
        if (uri.StartsWith(schemaPrefix, StringComparison.OrdinalIgnoreCase) && uri.EndsWith(schemaSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var datasetId = uri[schemaPrefix.Length..^schemaSuffix.Length];
            if (McpDatasetCatalog.TryParseDatasetId(datasetId, out var tableId) && discovery != null)
            {
                var detail = await discovery.GetDatasetDetailAsync(tableId, requestContext, ct).ConfigureAwait(false);
                if (detail != null)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"# Schema for `{detail.DatasetId}`\n");
                    sb.AppendLine($"- **Domain:** {detail.Domain}");
                    sb.AppendLine($"- **Table:** {detail.Table}");
                    sb.AppendLine($"- **Sensitivity:** {detail.Sensitivity}");
                    sb.AppendLine($"- **Active:** {detail.IsActive}\n");
                    sb.AppendLine("| Column | Type | Sensitivity | Masking | PK | PII |");
                    sb.AppendLine("|---|---|---|---|---|---|");
                    foreach (var c in detail.Columns)
                    {
                        sb.AppendLine($"| `{c.Name}` | {c.Type} | {c.Sensitivity} | {c.MaskingState} | {(c.IsPrimaryKey ? "✓" : "-")} | {(c.IsPiiIndicator ? "✓" : "-")} |");
                    }
                    return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = sb.ToString() }] };
                }
            }
            throw new McpProtocolException($"Resource '{uri}' not found.", McpErrorCode.ResourceNotFound);
        }

        // 3. autheris://catalog/datasources
        if (string.Equals(uri, "autheris://catalog/datasources", StringComparison.OrdinalIgnoreCase))
        {
            var datasources = discovery != null ? await discovery.ListDatasourcesAsync(requestContext, ct).ConfigureAwait(false) : [];
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Registered Enterprise Datasources\n");
            sb.AppendLine("| Datasource ID | Name | Domain | Type | Status | Configured |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var ds in datasources)
            {
                sb.AppendLine($"| `{ds.Id}` | {ds.Name} | {ds.Domain} | {ds.Type} | {ds.Status} | {(ds.IsConfigured ? "✓" : "-")} |");
            }
            return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = sb.ToString() }] };
        }

        // 4. autheris://governance/my-access
        if (string.Equals(uri, "autheris://governance/my-access", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Autheris Governance - Current Session Access Profile\n");
            sb.AppendLine($"- **Principal:** `{session.UserSid ?? session.ServicePrincipalId}`");
            sb.AppendLine($"- **Tenant:** `{session.TenantId}`");
            sb.AppendLine($"- **Roles:** {(session.Roles != null && session.Roles.Count > 0 ? string.Join(", ", session.Roles) : "None")}");
            sb.AppendLine($"- **Groups:** {(session.GroupSids != null && session.GroupSids.Count > 0 ? string.Join(", ", session.GroupSids) : "None")}");
            sb.AppendLine($"- **Read-Only:** {session.IsReadOnly}");
            return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = sb.ToString() }] };
        }

        // 5. autheris://api/openapi.json
        if (string.Equals(uri, "autheris://api/openapi.json", StringComparison.OrdinalIgnoreCase))
        {
            var json = dispatcher != null ? await dispatcher.GetOpenApiJsonAsync(ct).ConfigureAwait(false) : "{}";
            return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = json }] };
        }

        // 6. autheris://api/docs/endpoints
        if (string.Equals(uri, "autheris://api/docs/endpoints", StringComparison.OrdinalIgnoreCase))
        {
            var text = dispatcher != null ? await dispatcher.GetEndpointsDocumentationAsync(ct).ConfigureAwait(false) : "";
            return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = text }] };
        }

        // 7. autheris://api/docs/mcp-tools
        if (string.Equals(uri, "autheris://api/docs/mcp-tools", StringComparison.OrdinalIgnoreCase))
        {
            var text = dispatcher != null ? await dispatcher.GetMcpToolsDocumentationAsync(ct).ConfigureAwait(false) : "";
            return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = text }] };
        }

        throw new McpProtocolException($"Resource '{uri}' not found.", McpErrorCode.ResourceNotFound);
    }

    public static IReadOnlyList<Prompt> GetAvailablePrompts() =>
    [
        new Prompt
        {
            Name = "explore_dataset",
            Description = "Guides an agent through exploring dataset schema, metadata, sample rows, and query best practices.",
            Arguments =
            [
                new PromptArgument
                {
                    Name = "dataset",
                    Description = "Dataset identifier in domain.schema.table format (e.g. sales.public.orders)",
                    Required = true
                }
            ]
        },
        new Prompt
        {
            Name = "audit_access_compliance",
            Description = "Audits access permissions, column masking rules, and compliance requirements for a dataset and principal.",
            Arguments =
            [
                new PromptArgument
                {
                    Name = "dataset",
                    Description = "Dataset identifier in domain.schema.table format",
                    Required = true
                },
                new PromptArgument
                {
                    Name = "principal",
                    Description = "Optional user identity or role to audit (defaults to current caller)",
                    Required = false
                }
            ]
        }
    ];

    public static GetPromptResult BuildPrompt(string promptName, IReadOnlyDictionary<string, string?>? arguments)
    {
        var args = arguments ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(promptName, "explore_dataset", StringComparison.OrdinalIgnoreCase))
        {
            var ds = args.TryGetValue("dataset", out var d) && !string.IsNullOrWhiteSpace(d) ? d : "{dataset}";
            return new GetPromptResult
            {
                Description = $"Dataset Exploration Workflow for {ds}",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = ModelContextProtocol.Protocol.Role.User,
                        Content = new TextContentBlock
                        {
                            Text = $"I need to explore the dataset '{ds}'. Please:\n" +
                                   $"1. Call describe_dataset or get_my_permissions for '{ds}' to view accessible columns, data types, and masking rules.\n" +
                                   $"2. Call sample_rows with count=5 to inspect representative records.\n" +
                                   $"3. Recommend the best query strategy (query_sql, query_dataset, or query_graphql) including pagination and filter criteria."
                        }
                    }
                ]
            };
        }

        if (string.Equals(promptName, "audit_access_compliance", StringComparison.OrdinalIgnoreCase))
        {
            var ds = args.TryGetValue("dataset", out var d) && !string.IsNullOrWhiteSpace(d) ? d : "{dataset}";
            var principal = args.TryGetValue("principal", out var p) && !string.IsNullOrWhiteSpace(p) ? p : "current caller";
            return new GetPromptResult
            {
                Description = $"Access and Compliance Audit for {ds} (Principal: {principal})",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = ModelContextProtocol.Protocol.Role.User,
                        Content = new TextContentBlock
                        {
                            Text = $"Perform an access and compliance audit on dataset '{ds}' for principal '{principal}':\n" +
                                   $"1. Check effective ReBAC authorization and row-level security constraints.\n" +
                                   $"2. List all columns with their sensitivity classifications and masking states (clear, mask, deny).\n" +
                                   $"3. Inspect data lineage via get_data_lineage to identify downstream consumers and potential compliance risks."
                        }
                    }
                ]
            };
        }

        throw new McpProtocolException($"Prompt '{promptName}' not found.", McpErrorCode.InvalidParams);
    }

    private static async ValueTask<CallToolResult> CallGuardedToolAsync(IServiceProvider services, string toolName, string argumentsJson, CancellationToken ct)
    {
        var session = Caller(services);
        var guardrail = services.GetRequiredService<IAiDataGuardrailService>();
        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(toolName, argumentsJson, session.SessionId), session, ct).ConfigureAwait(false);

        // SEC M-17: structured executor error results are tool errors (isError:true).
        if (!result.IsSuccess || AiDataGuardrailService.TryGetExecutorError(result.ContentJson, out _))
        {
            var message = result.ErrorMessage ?? (result.IsSuccess ? result.ContentJson : null) ?? "Tool execution failed";
            return new CallToolResult { Content = [new TextContentBlock { Text = message }], IsError = true };
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = result.ContentJson }],
            IsError = false,
            Meta = new System.Text.Json.Nodes.JsonObject
            {
                ["isMasked"] = result.IsMasked,
                ["estimatedTokens"] = result.EstimatedTokens,
                ["truncated"] = result.TruncatedDueToBudget
            }
        };
    }

    /// <summary>SEC H-16: the caller of this request, from its authenticated principal (no session state is kept).</summary>
    private static McpSessionContext Caller(IServiceProvider services)
    {
        var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new McpProtocolException("No HTTP request context.", McpErrorCode.InternalError);
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
        var caller = McpEndpoints.ResolveCaller(httpContext, options.IsMcpAuthBypassed);
        var now = DateTimeOffset.UtcNow;
        return new McpSessionContext(
            "http-" + httpContext.TraceIdentifier,
            caller.PrincipalId,
            caller.TenantId,
            now,
            now,
            caller.UserSid,
            caller.Roles,
            caller.GroupSids,
            caller.ClientIp,
            caller.IsReadOnly,
            caller.AdditionalClaims);
    }

    /// <summary>Low (JSON injection): only well-formed object schemas are passed on; anything else becomes an empty object schema.</summary>
    private static JsonElement Schema(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyObjectSchema;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String &&
                   type.GetString() == "object"
                ? doc.RootElement.Clone()
                : EmptyObjectSchema;
        }
        catch (JsonException)
        {
            return EmptyObjectSchema;
        }
    }
}
