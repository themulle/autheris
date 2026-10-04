namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// F-AI-11: Enforces prompt injection defense and strict parameter validation on MCP tool invocations.
/// </summary>
public sealed class PersistedToolValidator : IPersistedToolValidator
{
    private static readonly string[] PromptInjectionKeywords =
    [
        "ignore all previous instructions",
        "ignore previous instructions",
        "disregard all previous instructions",
        "system prompt:",
        "developer override mode",
        "disregard authorization rules",
        "bypass rls",
        "bypass authorization",
        "dump database",
        "reveal all tenant records"
    ];

    public string ComputeOperationHash(string graphQlOperation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphQlOperation);

        // Normalize whitespace to generate canonical hash
        var normalized = Regex.Replace(graphQlOperation.Trim(), @"\s+", " ");
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public bool ValidateToolInvocation(McpToolDefinition tool, JsonElement arguments, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(tool);

        // 1. Check for prompt injection in all string values
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var stringVal = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(stringVal))
                    {
                        foreach (var keyword in PromptInjectionKeywords)
                        {
                            if (stringVal.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                failureReason = $"Prompt injection pattern detected in parameter '{prop.Name}': matched '{keyword}'.";
                                return false;
                            }
                        }
                    }
                }
            }
        }

        // 2. Strict parameter validation against InputJsonSchema
        if (!string.IsNullOrWhiteSpace(tool.InputJsonSchema) && arguments.ValueKind == JsonValueKind.Object)
        {
            try
            {
                using var schemaDoc = JsonDocument.Parse(tool.InputJsonSchema);
                var root = schemaDoc.RootElement;
                if (root.TryGetProperty("properties", out var propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object)
                {
                    var declaredProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in propertiesElement.EnumerateObject())
                    {
                        declaredProps.Add(prop.Name);
                    }

                    foreach (var argProp in arguments.EnumerateObject())
                    {
                        if (!declaredProps.Contains(argProp.Name))
                        {
                            failureReason = $"Undeclared parameter '{argProp.Name}' is not permitted for tool '{tool.Name}'. Extra parameters rejected.";
                            return false;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // If schema parsing fails, fail-closed
                failureReason = $"Failed to parse declared input schema for tool '{tool.Name}'.";
                return false;
            }
        }

        failureReason = null;
        return true;
    }
}
