namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

using Autheris.Domain.Common;

/// <summary>
/// Definition of a Model Context Protocol (MCP) tool exposed to AI agents.
/// </summary>
public sealed record McpToolDefinition(
    string Name,
    string Description,
    string InputJsonSchema,
    string TargetGraphQLOperation,
    TableIdentifier? TargetTable = null
);

/// <summary>
/// Incoming tool call request from an AI agent over JSON-RPC 2.0.
/// </summary>
public sealed record McpToolCallRequest(
    string ToolName,
    string ArgumentsJson,
    string? SessionId = null,
    string? RequestId = null
);

/// <summary>
/// Result of an MCP tool execution after applying AI Data Guardrails.
/// </summary>
public sealed record McpToolCallResult(
    bool IsSuccess,
    string ContentJson,
    string? ErrorMessage = null,
    int EstimatedTokens = 0,
    bool IsMasked = false,
    bool TruncatedDueToBudget = false
);

/// <summary>
/// Active MCP session context bound to an authenticated principal.
/// </summary>
public sealed record McpSessionContext(
    string SessionId,
    string ServicePrincipalId,
    string TenantId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    string? UserSid = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? GroupSids = null,
    string? ClientIp = null
);
