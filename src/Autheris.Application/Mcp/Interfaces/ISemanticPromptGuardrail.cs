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
}
