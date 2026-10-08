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
    private const string DatasetResourcePrefix = "autheris://datasets/";

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
            .WithListResourceTemplatesHandler(ListResourceTemplatesAsync);

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
        var compiler = context.Services!.GetService<ISemanticMcpCompiler>();
        if (compiler == null)
        {
            return new ListResourcesResult { Resources = [] };
        }

        var principal = McpProtocolHandler.BuildPrincipalFromSession(Caller(context.Services!));
        var resources = await compiler.GetSemanticResourcesAsync(null, principal, ct).ConfigureAwait(false);
        return new ListResourcesResult
        {
            Resources = resources.Select(r => new Resource { Uri = r.Uri, Name = r.Name, Description = r.Description, MimeType = r.MimeType }).ToList()
        };
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
                }
            ]
        });

    internal static async ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> context, CancellationToken ct)
    {
        var uri = context.Params?.Uri ?? throw new McpProtocolException("Missing resource uri.", McpErrorCode.InvalidParams);

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
            caller.ClientIp);
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
