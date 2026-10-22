namespace Autheris.Application.Mcp.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Guardrail engine enforcing zero-trust data protection, PII masking, token budgeting,
/// and GDPR Article 9 redaction before data is injected into an AI agent's context window.
/// </summary>
public interface IAiDataGuardrailService
{
    /// <summary>
    /// Executes a tool call under zero-trust governance and applies automated data scrubbing.
    /// </summary>
    /// <param name="request">The tool call request containing tool name and arguments.</param>
    /// <param name="sessionContext">The caller's MCP session context including tenant and service principal identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sanitized tool call result ready for consumption by an LLM.</returns>
    ValueTask<McpToolCallResult> ExecuteToolWithGuardrailAsync(
        McpToolCallRequest request,
        McpSessionContext sessionContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an MCP resource under the governance of a tool call: Casbin ABAC on every one of <paramref name="targetTables"/>, PII
    /// scrubbing, the token budget (when <paramref name="applyTokenBudget"/> is set) and an <c>MCP_TOOL_EXECUTION</c> audit row.
    /// <paramref name="readContent"/> runs only when every target is allowed; a <see cref="McpResourceNotFoundException"/> it raises is a
    /// failed result. The result carries the (scrubbed) resource text in <see cref="McpToolCallResult.ContentJson"/>. The default
    /// implementation denies every read (fail closed).
    /// </summary>
    ValueTask<McpToolCallResult> ReadResourceWithGuardrailAsync(
        string resourceUri,
        IReadOnlyList<TableIdentifier> targetTables,
        Func<CancellationToken, Task<string>> readContent,
        McpSessionContext sessionContext,
        bool applyTokenBudget = true,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Resource '{resourceUri}' not found."));
}
