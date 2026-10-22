namespace Autheris.Api.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mcp.Tools;
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
        var session = Caller(context.Services!);
        var options = context.Services!.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;

        // SR-H-02: admin tools are listed for the canonical cluster admin only (and never for read-only sessions).
        bool isAdmin = McpAdminTools.CanUseAdminTools(session, options.IsMcpAuthBypassed);

        var available = registry.GetAvailableTools();
        if (!isAdmin)
        {
            available = available.Where(t => !McpAdminTools.IsAdminTool(t.Name)).ToList();
        }

        var tools = available
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
        var switches = context.Services!.GetService<IReadPathSwitches>();
        var resources = FilterResources(NativeResourceList(), switches);

        var compiler = context.Services!.GetService<ISemanticMcpCompiler>();
        if (compiler != null)
        {
            var principal = McpProtocolHandler.BuildPrincipalFromSession(Caller(context.Services!));
            var semanticResources = await compiler.GetSemanticResourcesAsync(null, principal, ct).ConfigureAwait(false);
            resources.AddRange(semanticResources.Select(r => new Resource { Uri = r.Uri, Name = r.Name, Description = r.Description, MimeType = r.MimeType }));
        }

        return new ListResourcesResult { Resources = resources };
    }

    internal static List<Resource> NativeResourceList() =>
    [
        new() { Uri = "autheris://catalog/summary", Name = "Catalog Summary", Description = "Summary of visible enterprise catalog datasets", MimeType = "text/markdown" },
        new() { Uri = "autheris://catalog/datasources", Name = "Datasources", Description = "Status of connected enterprise data sources", MimeType = "text/markdown" },
        new() { Uri = "autheris://governance/my-access", Name = "My Access", Description = "Effective access profile, roles and policies of current caller", MimeType = "text/markdown" },
        new() { Uri = "autheris://api/openapi.json", Name = "OpenAPI Specification", Description = "OpenAPI 3.1 specification for Autheris REST endpoints", MimeType = "application/json" },
        new() { Uri = "autheris://api/docs/endpoints", Name = "API Endpoints Documentation", Description = "Markdown documentation of available REST endpoints", MimeType = "text/markdown" },
        new() { Uri = "autheris://api/docs/mcp-tools", Name = "MCP Tools Reference", Description = "Markdown reference for available MCP tools", MimeType = "text/markdown" }
    ];

    /// <summary>PLAN-SEC-115 A-6: omits the native resources whose group is switched off (the same gate <c>ReadAsync</c> applies).</summary>
    internal static List<Resource> FilterResources(IEnumerable<Resource> resources, IReadPathSwitches? switches) =>
        switches is null ? resources.ToList() : resources.Where(r => switches.IsResourceEnabled(r.Uri!)).ToList();

    internal static ValueTask<ListResourceTemplatesResult> ListResourceTemplatesAsync(RequestContext<ListResourceTemplatesRequestParams> context, CancellationToken ct) =>
        ValueTask.FromResult(new ListResourceTemplatesResult
        {
            ResourceTemplates = FilterResourceTemplates(NativeResourceTemplateList(), context.Services!.GetService<IReadPathSwitches>())
        });

    internal static List<ResourceTemplate> NativeResourceTemplateList() =>
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
    ];

    internal static List<ResourceTemplate> FilterResourceTemplates(IEnumerable<ResourceTemplate> templates, IReadPathSwitches? switches) =>
        switches is null ? templates.ToList() : templates.Where(t => switches.IsResourceEnabled(t.UriTemplate)).ToList();

    internal static async ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> context, CancellationToken ct)
    {
        var uri = context.Params?.Uri ?? throw new McpProtocolException("Missing resource uri.", McpErrorCode.InvalidParams);

        var resourceProvider = context.Services!.GetRequiredService<IMcpResourceProvider>();
        if (resourceProvider.IsNativeResource(uri))
        {
            return await ReadGuardedNativeResourceAsync(context.Services!, uri, resourceProvider, ct).ConfigureAwait(false);
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
        return await ReadGuardedSemanticResourceAsync(context.Services!, uri, compiler, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A native resource read through the guardrail (Casbin ABAC on the objects the resource discloses, PII scrubbing, token budget,
    /// MCP_TOOL_EXECUTION audit), like the dataset template's describe_dataset call. Unknown, closed and denied resources answer alike.
    /// </summary>
    internal static async Task<ReadResourceResult> ReadGuardedNativeResourceAsync(IServiceProvider services, string uri, IMcpResourceProvider provider, CancellationToken ct)
    {
        // PLAN-SEC-115 C-115-6: a closed resource answers like an unknown URI without reaching the guardrail or the provider.
        var targets = provider.IsResourceEnabled(uri) ? McpResourceProvider.GuardTargets(uri) : null;
        if (targets is null)
        {
            throw NotFound(uri);
        }

        var session = Caller(services);
        McpResourceContent? content = null;
        var result = await services.GetRequiredService<IAiDataGuardrailService>().ReadResourceWithGuardrailAsync(
            uri,
            targets,
            async token =>
            {
                content = await provider.ReadAsync(uri, session, token).ConfigureAwait(false);
                return content.Text;
            },
            session,
            applyTokenBudget: !McpResourceProvider.IsStaticDocumentation(uri),
            ct).ConfigureAwait(false);

        if (result is not { IsSuccess: true } || content is null)
        {
            throw NotFound(uri);
        }

        return new ReadResourceResult { Contents = [new TextResourceContents { Uri = content.Uri, MimeType = content.MimeType, Text = result.ContentJson }] };
    }

    /// <summary>
    /// A semantic resource (glossary, dbt, examples) read through the guardrail; its ABAC object is the semantic catalog, like the
    /// semantic tools. The resource list is consent-filtered for the caller; a URI outside it answers as not found.
    /// </summary>
    internal static async Task<ReadResourceResult> ReadGuardedSemanticResourceAsync(IServiceProvider services, string uri, ISemanticMcpCompiler compiler, CancellationToken ct)
    {
        var session = Caller(services);
        McpResourceItem? resource = null;
        var result = await services.GetRequiredService<IAiDataGuardrailService>().ReadResourceWithGuardrailAsync(
            uri,
            [McpSemanticTools.SemanticCatalogTable],
            async token =>
            {
                var principal = McpProtocolHandler.BuildPrincipalFromSession(session);
                var resources = await compiler.GetSemanticResourcesAsync(null, principal, token).ConfigureAwait(false);
                resource = resources.FirstOrDefault(r => string.Equals(r.Uri, uri, StringComparison.OrdinalIgnoreCase))
                    ?? throw new McpResourceNotFoundException(uri);
                return resource.Text;
            },
            session,
            applyTokenBudget: true,
            ct).ConfigureAwait(false);

        if (result is not { IsSuccess: true } || resource is null)
        {
            throw NotFound(uri);
        }

        return new ReadResourceResult { Contents = [new TextResourceContents { Uri = resource.Uri, MimeType = resource.MimeType, Text = result.ContentJson }] };
    }

    private static McpProtocolException NotFound(string uri) => new($"Resource '{uri}' not found.", McpErrorCode.ResourceNotFound);

    internal static ValueTask<ListPromptsResult> ListPromptsAsync(RequestContext<ListPromptsRequestParams> context, CancellationToken ct) =>
        ValueTask.FromResult(new ListPromptsResult { Prompts = GetAvailablePrompts(context.Services!.GetService<IReadPathSwitches>()).ToList() });

    internal static ValueTask<GetPromptResult> GetPromptAsync(RequestContext<GetPromptRequestParams> context, CancellationToken ct)
    {
        var request = context.Params ?? throw new McpProtocolException("Missing get prompt parameters.", McpErrorCode.InvalidParams);
        var stringArgs = request.Arguments?.ToDictionary(
            k => k.Key,
            v => v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var result = BuildPrompt(request.Name, stringArgs, context.Services!.GetService<IReadPathSwitches>());
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// The provider read mapped to the SDK types, without the guardrail. The request path uses <see cref="ReadGuardedNativeResourceAsync"/>;
    /// this helper only maps the provider's not-found answer.
    /// </summary>
    internal static async Task<ReadResourceResult> ReadNativeResourceAsync(string uri, IMcpResourceProvider provider, McpSessionContext session, CancellationToken ct = default)
    {
        try
        {
            var content = await provider.ReadAsync(uri, session, ct).ConfigureAwait(false);
            return new ReadResourceResult { Contents = [new TextResourceContents { Uri = content.Uri, MimeType = content.MimeType, Text = content.Text }] };
        }
        catch (McpResourceNotFoundException)
        {
            throw new McpProtocolException($"Resource '{uri}' not found.", McpErrorCode.ResourceNotFound);
        }
    }

    /// <summary>The prompts the caller may see; a prompt whose tool groups are switched off is unlisted (hygiene, not a security control).</summary>
    public static IReadOnlyList<Prompt> GetAvailablePrompts(IReadPathSwitches? switches = null) =>
        switches is null
            ? AllPrompts()
            : AllPrompts().Where(p => switches.IsPromptEnabled(p.Name)).ToList();

    private static List<Prompt> AllPrompts() =>
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

    public static GetPromptResult BuildPrompt(string promptName, IReadOnlyDictionary<string, string?>? arguments, IReadPathSwitches? switches = null)
    {
        // A gated prompt answers exactly like an unknown one (PLAN-SEC-115 ST-115-14).
        if (switches is not null && !switches.IsPromptEnabled(promptName))
        {
            throw new McpProtocolException($"Prompt '{promptName}' not found.", McpErrorCode.InvalidParams);
        }

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
        var caller = McpEndpoints.ResolveCaller(httpContext, options.IsMcpAuthBypassed, options.CallerAttributes);
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
    internal static JsonElement Schema(string? json)
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
