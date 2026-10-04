namespace Autheris.Application.Mcp.Diagnostics;

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// OpenTelemetry Diagnostics Provider compliant with OpenTelemetry GenAI Semantic Conventions (gen_ai.*).
/// Provides ActivitySource tracing and Meter metrics for AI agent tool invocations, token consumption,
/// and Zero-Trust Guardrail evaluations.
/// </summary>
public static class McpDiagnostics
{
    public const string ActivitySourceName = "Autheris.Mcp";
    public const string MeterName = "Autheris.Mcp";
    public const string Version = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);
    public static readonly Meter Meter = new(MeterName, Version);

    // GenAI Semantic Metric Instruments
    public static readonly Counter<long> TokenUsageCounter = Meter.CreateCounter<long>(
        "gen_ai.client.token.usage",
        unit: "{token}",
        description: "Measures number of input and output tokens consumed during AI agent tool execution.");

    public static readonly Counter<long> GuardrailEventsCounter = Meter.CreateCounter<long>(
        "gen_ai.guardrail.events",
        unit: "{event}",
        description: "Measures number of Guardrail policy evaluations (allow, deny, masked).");

    public static readonly Histogram<double> ToolExecutionDuration = Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration",
        unit: "s",
        description: "Duration of AI tool execution and guardrail pipeline in seconds.");

    // Semantic Attribute Key Constants
    public const string GenAiSystemKey = "gen_ai.system";
    public const string GenAiOperationNameKey = "gen_ai.operation.name";
    public const string GenAiToolNameKey = "gen_ai.tool.name";
    public const string GenAiToolCallIdKey = "gen_ai.tool.call.id";
    public const string GenAiUsageInputTokensKey = "gen_ai.usage.input_tokens";
    public const string GenAiUsageOutputTokensKey = "gen_ai.usage.output_tokens";
    public const string GenAiGuardrailVerdictKey = "gen_ai.guardrail.verdict";
    public const string GenAiGuardrailPiiDetectedKey = "gen_ai.guardrail.pii_detected";
    public const string GenAiGuardrailArt9DetectedKey = "gen_ai.guardrail.art9_detected";
    public const string GenAiClientIdKey = "gen_ai.client.id";

    public static void RecordTokenUsage(long tokens, string tokenType, string toolName)
    {
        if (tokens > 0)
        {
            TokenUsageCounter.Add(tokens,
                new KeyValuePair<string, object?>("token_type", tokenType),
                new KeyValuePair<string, object?>("tool_name", toolName));
        }
    }

    public static void RecordGuardrailVerdict(string verdict, string toolName, bool piiDetected, bool art9Detected)
    {
        GuardrailEventsCounter.Add(1,
            new KeyValuePair<string, object?>("verdict", verdict),
            new KeyValuePair<string, object?>("tool_name", toolName),
            new KeyValuePair<string, object?>("pii_detected", piiDetected),
            new KeyValuePair<string, object?>("art9_detected", art9Detected));
    }
}
