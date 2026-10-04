namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Mcp.Interfaces;
using Microsoft.Extensions.Logging;

using System.Buffers;

/// <summary>
/// Semantic Prompt-Injection and Jailbreak Guardrail engine (inspired by NeMo Guardrails / Llama Guard heuristics).
/// Analyzes tool arguments and prompt strings for prompt injection, jailbreak personas, delimiter escapes,
/// and obfuscated malicious instructions prior to tool execution.
/// </summary>
public sealed partial class SemanticPromptGuardrail(ILogger<SemanticPromptGuardrail>? logger = null) : ISemanticPromptGuardrail
{
    private readonly ILogger<SemanticPromptGuardrail>? _logger = logger;

    // 1. Direct Instruction Overrides
    [GeneratedRegex(
        @"(?:ignore|disregard|forget|bypass|override)\s+(?:all\s+)?(?:previous|prior|preceding|system|all)\s+(?:instructions|directives|prompts|rules|guidelines|guardrails)|(?:output|print|reveal|show|dump)\s+(?:the\s+)?(?:system\s+prompt|initial\s+instructions)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex DirectInstructionOverrideRegex();

    // 2. Jailbreak Personas & Modes (DAN, Developer Mode, AIM, Unrestricted AI)
    [GeneratedRegex(
        @"\b(?:DAN\s+mode|Do\s+Anything\s+Now|Developer\s+Mode(?:\s+v[0-9]|\s+enabled|\s+active|\s+now)?|AIM\s+persona|unfiltered\s+assistant|evil\s+confidant|jailbreak\s+mode|always\s+intelligent\s+and\s+machiavellian)\b|(?:pretend|act|simulate|roleplay)\s+(?:you\s+are|as)\s+(?:an?\s+)?(?:unrestricted|unaligned|evil|godmode|jailbroken)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex JailbreakPersonaRegex();

    // 3. Instruction Delimiter Escapes (ChatML, Llama, Anthropic prompt injection syntax)
    [GeneratedRegex(
        @"(?:\[INST\]|\[/INST\]|<\|im_start\|>|<\|im_end\|>|<\|system\|>|###\s*(?:INSTRUCTION|SYSTEM|HUMAN|ASSISTANT)|<system>|---\s*BEGIN\s+SYSTEM\s+PROMPT)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex InstructionDelimiterRegex();

    // 4. Exfiltration and Admin Coercion
    [GeneratedRegex(
        @"(?:exfiltrate|leak|export\s+all|dump\s+database|drop\s+table|grant\s+admin|elevate\s+privileges|disable\s+guardrails)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ExfiltrationCoercionRegex();

    // 5. Potential Base64 Segments
    [GeneratedRegex(
        @"(?:[A-Za-z0-9+/]{4}){6,}(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?",
        RegexOptions.None,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex Base64SegmentRegex();

    public PromptGuardrailEvaluation EvaluatePrompt(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return PromptGuardrailEvaluation.Allow();
        }

        // SEC M-29: evaluate the DECODED argument values (JSON escapes such as \u0069gnore resolved), NFKC-normalised
        // and stripped of zero-width / bidi control characters. The raw text is checked as well. This guard remains
        // a heuristic, not a security boundary.
        var candidates = new List<string>(2) { NormalizeForInspection(argumentsJson) };
        var decoded = ExtractDecodedText(argumentsJson);
        if (decoded != null)
        {
            candidates.Add(decoded);
        }

        foreach (var candidate in candidates)
        {
            var evaluation = EvaluateText(toolName, candidate);
            if (!evaluation.IsAllowed)
            {
                return evaluation;
            }
        }

        return PromptGuardrailEvaluation.Allow();
    }

    private PromptGuardrailEvaluation EvaluateText(string toolName, string text)
    {
        // Direct Text Checks
        if (DirectInstructionOverrideRegex().IsMatch(text))
        {
            _logger?.LogWarning("Prompt injection detected in tool '{ToolName}': Direct instruction override attempt.", toolName);
            return PromptGuardrailEvaluation.Deny("DirectInstructionOverride", "Prompt contains prohibited direct instruction override or system prompt exfiltration attempt.");
        }

        if (JailbreakPersonaRegex().IsMatch(text))
        {
            _logger?.LogWarning("Jailbreak attempt detected in tool '{ToolName}': Known persona or adversarial roleplay.", toolName);
            return PromptGuardrailEvaluation.Deny("JailbreakPersona", "Prompt contains adversarial jailbreak or persona-coercion pattern (e.g. DAN / Developer Mode).");
        }

        if (InstructionDelimiterRegex().IsMatch(text))
        {
            _logger?.LogWarning("Delimiter injection detected in tool '{ToolName}': Adversarial prompt boundary markers.", toolName);
            return PromptGuardrailEvaluation.Deny("InstructionDelimiterEscape", "Prompt contains adversarial prompt boundary markers or synthetic delimiter tokens.");
        }

        if (ExfiltrationCoercionRegex().IsMatch(text))
        {
            _logger?.LogWarning("Coercive exfiltration pattern detected in tool '{ToolName}'.", toolName);
            return PromptGuardrailEvaluation.Deny("PrivilegeCoercion", "Prompt contains malicious privilege elevation or exfiltration instruction.");
        }

        // Check for Base64 obfuscated injections
        var base64Evaluation = CheckBase64Payloads(text);
        if (!base64Evaluation.IsAllowed)
        {
            _logger?.LogWarning("Obfuscated base64 prompt injection detected in tool '{ToolName}': {Reason}", toolName, base64Evaluation.Reason);
            return base64Evaluation;
        }

        return PromptGuardrailEvaluation.Allow();
    }

    /// <summary>
    /// Decodes the JSON arguments and concatenates all property names and string values (recursively),
    /// each normalised via <see cref="NormalizeForInspection"/>. Returns null if the input is not JSON.
    /// </summary>
    internal static string? ExtractDecodedText(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var sb = new StringBuilder();
            AppendStrings(doc.RootElement, sb);
            return sb.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendStrings(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append(NormalizeForInspection(element.GetString() ?? string.Empty)).Append('\n');
                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    sb.Append(NormalizeForInspection(prop.Name)).Append('\n');
                    AppendStrings(prop.Value, sb);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendStrings(item, sb);
                }
                break;
        }
    }

    /// <summary>NFKC normalisation plus removal of zero-width, bidi-control and other invisible format characters.</summary>
    internal static string NormalizeForInspection(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Invalid surrogate sequences cannot be normalised; inspect the raw value.
            normalized = value;
        }

        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (IsInvisibleControl(c))
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsInvisibleControl(char c) =>
        c is '\u00AD' or '\u034F' or '\u061C' or '\u115F' or '\u1160' or '\u17B4' or '\u17B5' or '\u180E' or '\u3164' or '\uFEFF' or '\uFFA0'
        || (c >= '\u200B' && c <= '\u200F')
        || (c >= '\u202A' && c <= '\u202E')
        || (c >= '\u2060' && c <= '\u206F')
        || (c >= '\uFE00' && c <= '\uFE0F')
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    private static PromptGuardrailEvaluation CheckBase64Payloads(string text)
    {
        // Find potential base64 segments of length >= 24
        var matches = Base64SegmentRegex().Matches(text);
        Span<byte> buffer = stackalloc byte[512];
        foreach (Match match in matches)
        {
            var matchSpan = match.ValueSpan;
            var maxByteCount = (matchSpan.Length * 3) / 4;
            byte[]? rented = null;
            Span<byte> dest = maxByteCount <= buffer.Length ? buffer : (rented = ArrayPool<byte>.Shared.Rent(maxByteCount));

            try
            {
                if (Convert.TryFromBase64Chars(matchSpan, dest, out var bytesWritten) && bytesWritten >= 12)
                {
                    var decoded = Encoding.UTF8.GetString(dest[..bytesWritten]);
                    if (DirectInstructionOverrideRegex().IsMatch(decoded) ||
                        JailbreakPersonaRegex().IsMatch(decoded) ||
                        InstructionDelimiterRegex().IsMatch(decoded))
                    {
                        return PromptGuardrailEvaluation.Deny("Base64ObfuscatedInjection", $"Payload contains base64-obfuscated prompt injection directive: '{decoded[..Math.Min(40, decoded.Length)]}...'");
                    }
                }
            }
            catch
            {
                // Not valid base64 or not UTF-8, ignore
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        return PromptGuardrailEvaluation.Allow();
    }
}
