namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// JSON-RPC 2.0 Protocol Handler implementing the Model Context Protocol (MCP) specification (2024-11-05).
/// Scoped service executing within individual HTTP request contexts.
/// </summary>
public sealed class McpProtocolHandler : IMcpProtocolHandler
{
    private readonly IMcpSessionStore _sessionStore;
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly IAiDataGuardrailService _guardrailService;
    private readonly ISemanticMcpCompiler? _semanticCompiler;
    private readonly Autheris.Application.Mcp.Pruning.ISemanticToolPruner? _toolPruner;
    private readonly ILogger<McpProtocolHandler> _logger;

    public McpProtocolHandler(
        IMcpSessionStore sessionStore,
        IMcpToolRegistry toolRegistry,
        IAiDataGuardrailService guardrailService,
        ILogger<McpProtocolHandler> logger,
        ISemanticMcpCompiler? semanticCompiler = null,
        Autheris.Application.Mcp.Pruning.ISemanticToolPruner? toolPruner = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _guardrailService = guardrailService ?? throw new ArgumentNullException(nameof(guardrailService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _semanticCompiler = semanticCompiler;
        _toolPruner = toolPruner;
    }

    public McpSessionContext CreateSession(string servicePrincipalId, string tenantId)
        => CreateSession(servicePrincipalId, tenantId, null, null, null, null);

    public McpSessionContext CreateSession(
        string servicePrincipalId,
        string tenantId,
        string? userSid = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? groupSids = null,
        string? clientIp = null)
    {
        return _sessionStore.CreateSession(servicePrincipalId, tenantId, userSid, roles, groupSids, clientIp);
    }

    public McpSessionContext? GetSession(string sessionId)
    {
        return _sessionStore.GetSession(sessionId);
    }

    public ValueTask<McpSessionContext?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _sessionStore.GetSessionAsync(sessionId, cancellationToken);

    public bool RemoveSession(string sessionId)
    {
        return _sessionStore.RemoveSession(sessionId);
    }

    public async ValueTask<string> HandleMessageAsync(
        string sessionId,
        string jsonRpcPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonRpcPayload);

        var session = await _sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session == null)
        {
            return CreateErrorResponse(null, -32000, $"Invalid or expired MCP session '{sessionId}'.");
        }

        using var doc = JsonDocument.Parse(jsonRpcPayload);
        var root = doc.RootElement;

        // Extract RPC ID (can be string, number, or null)
        object? rpcId = null;
        if (root.TryGetProperty("id", out var idProp))
        {
            rpcId = idProp.ValueKind switch
            {
                JsonValueKind.Number => idProp.GetInt64(),
                JsonValueKind.String => idProp.GetString(),
                _ => null
            };
        }

        if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
        {
            return CreateErrorResponse(rpcId, -32600, "Invalid Request: 'method' must be a string.");
        }

        var method = methodProp.GetString();

        return method switch
        {
            "initialize" => HandleInitialize(rpcId),
            "ping" => HandlePing(rpcId),
            "tools/list" => await HandleToolsListAsync(rpcId, root, cancellationToken).ConfigureAwait(false),
            "tools/call" => await HandleToolsCallAsync(rpcId, root, session, cancellationToken).ConfigureAwait(false),
            "resources/list" => await HandleResourcesListAsync(rpcId, session, cancellationToken).ConfigureAwait(false),
            "resources/read" => await HandleResourcesReadAsync(rpcId, root, session, cancellationToken).ConfigureAwait(false),
            _ => CreateErrorResponse(rpcId, -32601, $"Method '{method}' not found.")
        };
    }

    private static string HandleInitialize(object? id)
    {
        return $$"""
        {
          "jsonrpc": "2.0",
          "id": {{FormatId(id)}},
          "result": {
            "protocolVersion": "2024-11-05",
            "serverInfo": {
              "name": "Autheris.McpServer",
              "version": "1.4.0"
            },
            "capabilities": {
              "tools": { "listChanged": false },
              "resources": { "subscribe": false, "listChanged": false }
            }
          }
        }
        """;
    }

    private static string HandlePing(object? id)
    {
        return $$"""
        {
          "jsonrpc": "2.0",
          "id": {{FormatId(id)}},
          "result": {}
        }
        """;
    }

    private async ValueTask<string> HandleToolsListAsync(
        object? id,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tools = _toolRegistry.GetAvailableTools();

        // F-AI-07: Extract prompt if provided in params for dynamic Just-in-Time pruning
        string? prompt = null;
        if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object)
        {
            if (paramsProp.TryGetProperty("prompt", out var promptProp) && promptProp.ValueKind == JsonValueKind.String)
            {
                prompt = promptProp.GetString();
            }
            else if (paramsProp.TryGetProperty("query", out var queryProp) && queryProp.ValueKind == JsonValueKind.String)
            {
                prompt = queryProp.GetString();
            }
        }

        if (_toolPruner != null && !string.IsNullOrWhiteSpace(prompt))
        {
            tools = await _toolPruner.PruneToolsAsync(prompt, tools, null, cancellationToken).ConfigureAwait(false);
        }

        var toolItems = new List<string>(tools.Count);

        foreach (var t in tools)
        {
            toolItems.Add($$"""
            {
              "name": "{{EscapeJson(t.Name)}}",
              "description": "{{EscapeJson(t.Description)}}",
              "inputSchema": {{SafeRawJson(t.InputJsonSchema)}}
            }
            """);
        }

        var toolsJsonArray = string.Join(",", toolItems);

        return $$"""
        {
          "jsonrpc": "2.0",
          "id": {{FormatId(id)}},
          "result": {
            "tools": [{{toolsJsonArray}}]
          }
        }
        """;
    }

    private async ValueTask<string> HandleToolsCallAsync(
        object? id,
        JsonElement root,
        McpSessionContext session,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("params", out var paramsProp) || paramsProp.ValueKind != JsonValueKind.Object)
        {
            return CreateErrorResponse(id, -32602, "Invalid params for 'tools/call': Expected an object.");
        }

        if (!paramsProp.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            return CreateErrorResponse(id, -32602, "Missing 'name' in tools/call params.");
        }

        var toolName = nameProp.GetString()!;
        var argsJson = paramsProp.TryGetProperty("arguments", out var argsProp)
            ? argsProp.GetRawText()
            : "{}";

        var request = new McpToolCallRequest(toolName, argsJson, session.SessionId, id?.ToString());
        var result = await _guardrailService.ExecuteToolWithGuardrailAsync(request, session, cancellationToken).ConfigureAwait(false);

        // SEC M-17: Structured executor error results must be flagged as tool errors (isError:true).
        if (!result.IsSuccess || AiDataGuardrailService.TryGetExecutorError(result.ContentJson, out _))
        {
            return $$"""
            {
              "jsonrpc": "2.0",
              "id": {{FormatId(id)}},
              "result": {
                "content": [
                  {
                    "type": "text",
                    "text": "{{EscapeJson(result.ErrorMessage ?? (result.IsSuccess ? result.ContentJson : null) ?? "Tool execution failed")}}"
                  }
                ],
                "isError": true
              }
            }
            """;
        }

        return $$"""
        {
          "jsonrpc": "2.0",
          "id": {{FormatId(id)}},
          "result": {
            "content": [
              {
                "type": "text",
                "text": "{{EscapeJson(result.ContentJson)}}"
              }
            ],
            "isError": false,
            "_meta": {
              "isMasked": {{result.IsMasked.ToString().ToLowerInvariant()}},
              "estimatedTokens": {{result.EstimatedTokens}},
              "truncated": {{result.TruncatedDueToBudget.ToString().ToLowerInvariant()}}
            }
          }
        }
        """;
    }

    private static string CreateErrorResponse(object? id, int code, string message)
    {
        return $$"""
        {
          "jsonrpc": "2.0",
          "id": {{FormatId(id)}},
          "error": {
            "code": {{code}},
            "message": "{{EscapeJson(message)}}"
          }
        }
        """;
    }

    private static string FormatId(object? id)
    {
        return id switch
        {
            long num => num.ToString(),
            int num => num.ToString(),
            string str => $"\"{EscapeJson(str)}\"",
            _ => "null"
        };
    }

    /// <summary>
    /// Low (JSON injection): only embed well-formed JSON documents verbatim; anything else becomes an empty schema.
    /// </summary>
    private static string SafeRawJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static string EscapeJson(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var serialized = JsonSerializer.Serialize(text);
        return serialized.Length >= 2 && serialized[0] == '"' && serialized[^1] == '"'
            ? serialized[1..^1]
            : serialized;
    }

    private static System.Security.Claims.ClaimsPrincipal? BuildPrincipalFromSession(McpSessionContext? session)
    {
        if (session == null) return null;
        var identity = new System.Security.Claims.ClaimsIdentity("MCP");
        if (!string.IsNullOrWhiteSpace(session.UserSid))
        {
            identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, session.UserSid));
            identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, session.UserSid));
        }
        if (!string.IsNullOrWhiteSpace(session.TenantId))
        {
            identity.AddClaim(new System.Security.Claims.Claim("tenant_id", session.TenantId));
        }
        if (session.Roles != null)
        {
            foreach (var r in session.Roles)
            {
                identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, r));
            }
        }
        if (session.GroupSids != null)
        {
            foreach (var g in session.GroupSids)
            {
                identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.GroupSid, g));
            }
        }
        return new System.Security.Claims.ClaimsPrincipal(identity);
    }

    private async Task<string> HandleResourcesListAsync(object? id, McpSessionContext? session, CancellationToken ct)
    {
        if (_semanticCompiler == null)
        {
            return $$"""
            {
              "jsonrpc": "2.0",
              "id": {{FormatId(id)}},
              "result": { "resources": [] }
            }
            """;
        }

        var principal = BuildPrincipalFromSession(session);
        var resources = await _semanticCompiler.GetSemanticResourcesAsync(null, principal, ct).ConfigureAwait(false);
        var items = new List<string>(resources.Count);
        foreach (var r in resources)
        {
            items.Add($$"""
            {
              "uri": "{{EscapeJson(r.Uri)}}",
              "name": "{{EscapeJson(r.Name)}}",
              "description": "{{EscapeJson(r.Description)}}",
              "mimeType": "{{EscapeJson(r.MimeType)}}"
            }
            """);
        }

        return $$"""
        {
          "jsonrpc": "2.0",
          "id": {{FormatId(id)}},
          "result": {
            "resources": [{{string.Join(",", items)}}]
          }
        }
        """;
    }

    private async Task<string> HandleResourcesReadAsync(object? id, JsonElement root, McpSessionContext? session, CancellationToken ct)
    {
        if (_semanticCompiler == null)
        {
            return CreateErrorResponse(id, -32602, "Semantic resources provider not configured.");
        }

        if (!root.TryGetProperty("params", out var p) ||
            !p.TryGetProperty("uri", out var uriProp) ||
            uriProp.ValueKind != JsonValueKind.String)
        {
            return CreateErrorResponse(id, -32602, "Invalid params: 'uri' string parameter required.");
        }

        var uri = uriProp.GetString();
        var principal = BuildPrincipalFromSession(session);
        var allResources = await _semanticCompiler.GetSemanticResourcesAsync(null, principal, ct).ConfigureAwait(false);
        var target = allResources.FirstOrDefault(r => string.Equals(r.Uri, uri, StringComparison.OrdinalIgnoreCase));

        if (target == null)
        {
            return CreateErrorResponse(id, -32004, $"Resource '{uri}' not found.");
        }

        return $$"""
        {
          "jsonrpc": "2.0",
          "id": {{FormatId(id)}},
          "result": {
            "contents": [
              {
                "uri": "{{EscapeJson(target.Uri)}}",
                "mimeType": "{{EscapeJson(target.MimeType)}}",
                "text": "{{EscapeJson(target.Text)}}"
              }
            ]
          }
        }
        """;
    }
}
