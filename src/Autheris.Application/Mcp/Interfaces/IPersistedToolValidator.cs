namespace Autheris.Application.Mcp.Interfaces;

using System.Text.Json;
using Autheris.Domain.Model;

/// <summary>
/// F-AI-11: Validates MCP tool calls against curated persisted operations and enforces OWASP LLM01 prompt injection shields.
/// </summary>
public interface IPersistedToolValidator
{
    bool ValidateToolInvocation(McpToolDefinition tool, JsonElement arguments, out string? failureReason);

    string ComputeOperationHash(string graphQlOperation);
}
