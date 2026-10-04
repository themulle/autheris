namespace Autheris.Application.Security;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public static class ChunkPiiRedactor
{
    private static readonly Regex EmailRegex = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex CreditCardRegex = new(@"\b(?:\d{4}[ -]?){3}\d{4}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex ApiKeyRegex = new(@"\b(?:ak_live|sk_live|secret)_[0-9a-zA-Z]{16,}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    // Prompt injection markers / control tokens
    private static readonly string[] DangerousPromptTokens = new[]
    {
        "<|im_start|>", "<|im_end|>", "<|system|>", "<|user|>", "<|assistant|>",
        "[INST]", "[/INST]", "<<SYS>>", "<</SYS>>"
    };

    public static VectorDocumentChunk RedactChunk(
        VectorDocumentChunk chunk,
        TableMetadata? metadata = null,
        IColumnMaskingProvider? maskingProvider = null)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var sanitizedText = chunk.ContentText;

        // 1. Sanitize dangerous prompt injection delimiter tokens
        foreach (var token in DangerousPromptTokens)
        {
            if (sanitizedText.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                sanitizedText = sanitizedText.Replace(token, "[SANITIZED_PROMPT_DELIMITER]", StringComparison.OrdinalIgnoreCase);
            }
        }

        // 2. Redact high-risk PII patterns
        sanitizedText = EmailRegex.Replace(sanitizedText, "[REDACTED_EMAIL]");
        sanitizedText = CreditCardRegex.Replace(sanitizedText, "[REDACTED_CREDIT_CARD]");
        sanitizedText = ApiKeyRegex.Replace(sanitizedText, "[REDACTED_SECRET_KEY]");

        // 3. Metadata sanitization
        var sanitizedMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in chunk.Metadata)
        {
            if (k.Contains("ssn", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                sanitizedMetadata[k] = "[REDACTED]";
            }
            else
            {
                sanitizedMetadata[k] = v;
            }
        }

        return chunk with
        {
            ContentText = sanitizedText,
            Metadata = sanitizedMetadata
        };
    }
}
