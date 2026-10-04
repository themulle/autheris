namespace Autheris.Application.Mcp.Interfaces;

using System.Threading;
using System.Threading.Tasks;
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
}
