namespace Autheris.Application.Mcp.Services;

using System;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Outcome of <see cref="AgentOutputGuard.Apply"/>.</summary>
/// <param name="Body">The guarded body (the input instance when nothing changed).</param>
/// <param name="InjectionWithheld">Number of string values replaced because of a suspected prompt injection.</param>
/// <param name="PiiScrubbed">True when e-mail, IBAN or GDPR Art. 9 values were masked.</param>
public readonly record struct AgentOutputGuardResult(string Body, int InjectionWithheld, bool PiiScrubbed)
{
    public bool Modified => InjectionWithheld > 0 || PiiScrubbed;
}

/// <summary>
/// Output guardrails for data returned to an autonomous agent outside MCP (GraphQL, WebSQL): the same prompt-injection sanitizing
/// and PII scrubbing the MCP path applies, so protection depends on the token scope, not on the transport.
/// </summary>
public sealed class AgentOutputGuard
{
    private readonly ISemanticPromptGuardrail _promptGuardrail;
    private readonly IOptions<GatewayOptions> _options;

    public AgentOutputGuard(ISemanticPromptGuardrail promptGuardrail, IOptions<GatewayOptions> options)
    {
        _promptGuardrail = promptGuardrail ?? throw new ArgumentNullException(nameof(promptGuardrail));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Sanitizes injection payloads, then scrubs PII unless unmasked AI access is explicitly allowed.</summary>
    public AgentOutputGuardResult Apply(string surface, string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new AgentOutputGuardResult(json, 0, false);
        }

        var body = _promptGuardrail.SanitizeToolOutput(surface, json, out var withheld, out _);
        if (withheld == 0 || body is null)
        {
            body = json;
            withheld = 0;
        }

        var scrubbed = false;
        if (!_options.Value.IsMcpUnmaskedAllowed)
        {
            body = AiDataGuardrailService.ScrubPiiAndSensitiveData(body, out scrubbed);
        }

        return new AgentOutputGuardResult(body, withheld, scrubbed);
    }
}
