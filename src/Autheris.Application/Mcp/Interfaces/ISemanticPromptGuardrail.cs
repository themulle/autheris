namespace Autheris.Application.Mcp.Interfaces;

public sealed record PromptGuardrailEvaluation(
    bool IsAllowed,
    string? AttackType = null,
    string? Reason = null
)
{
    public static PromptGuardrailEvaluation Allow() => new(true);
    public static PromptGuardrailEvaluation Deny(string attackType, string reason) => new(false, attackType, reason);
}

public interface ISemanticPromptGuardrail
{
    PromptGuardrailEvaluation EvaluatePrompt(string toolName, string? argumentsJson);

    /// <summary>
    /// Inspects the string values of a tool or resource result for prompt injection (MCP-09). The result is never denied: a poisoned
    /// text must not be usable to block catalog access. <see cref="PromptGuardrailEvaluation.IsAllowed"/> is false when at least one
    /// value matched. The default implementation inspects nothing.
    /// </summary>
    PromptGuardrailEvaluation EvaluateToolOutput(string toolName, string resultJson) => PromptGuardrailEvaluation.Allow();

    /// <summary>
    /// The result with every string value that matched replaced by a fixed withheld marker. <paramref name="withheldCount"/> is the number
    /// of replaced values and <paramref name="attackType"/> the first attack type found. The default implementation returns the result unchanged.
    /// </summary>
    string SanitizeToolOutput(string toolName, string resultJson, out int withheldCount, out string? attackType)
    {
        withheldCount = 0;
        attackType = null;
        return resultJson;
    }
}
