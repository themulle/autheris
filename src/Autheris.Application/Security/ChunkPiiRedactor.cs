namespace Autheris.Application.Security;

using System;
using System.Collections.Generic;
using System.Linq;
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
        IColumnMaskingProvider? maskingProvider = null,
        TableAccessDecision? decision = null,
        string? defaultHmacKeyId = null)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var sanitizedText = chunk.ContentText;

        // SEC E-1: Apply catalog column governance and access decision to content_text
        if (metadata != null && decision != null)
        {
            var contentCol = metadata.GetColumn("content_text") ?? metadata.GetColumn("content");
            var colName = contentCol?.ColumnName ?? "content_text";
            var contentAccess = decision.GetEffectiveColumnAccess(colName, metadata);

            if (contentAccess == ColumnAccessLevel.Deny)
            {
                sanitizedText = "[ACCESS_DENIED]";
            }
            else if (contentAccess == ColumnAccessLevel.Mask)
            {
                if (maskingProvider != null &&
                    (metadata.ColumnMaskingRules.TryGetValue(colName, out var maskRule) ||
                     (contentCol != null && metadata.ColumnMaskingRules.TryGetValue(contentCol.ColumnName, out maskRule))))
                {
                    sanitizedText = maskingProvider.MaskValue(colName, sanitizedText, Autheris.Application.Services.GatewayExecutionService.ScopeRuleForTenant(maskRule, chunk.TenantId.Value, defaultHmacKeyId))?.ToString() ?? "[REDACTED]";
                }
                else
                {
                    sanitizedText = "[MASKED]";
                }
            }
        }

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

        // 3. Metadata governance & sanitization (SEC E-1)
        var sanitizedMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in chunk.Metadata)
        {
            if (metadata != null && decision != null)
            {
                // Discard keys without catalog entry when catalog columns are defined
                if (metadata.Columns.Count > 0 && metadata.GetColumn(k) == null)
                {
                    continue;
                }

                var colAccess = decision.GetEffectiveColumnAccess(k, metadata);
                if (colAccess == ColumnAccessLevel.Deny)
                {
                    continue;
                }

                if (colAccess == ColumnAccessLevel.Mask)
                {
                    if (maskingProvider != null &&
                        (metadata.ColumnMaskingRules.TryGetValue(k, out var mRule) ||
                         (metadata.GetColumn(k) != null && metadata.ColumnMaskingRules.TryGetValue(metadata.GetColumn(k)!.ColumnName, out mRule))))
                    {
                        sanitizedMetadata[k] = maskingProvider.MaskValue(k, v, Autheris.Application.Services.GatewayExecutionService.ScopeRuleForTenant(mRule, chunk.TenantId.Value, defaultHmacKeyId));
                    }
                    else
                    {
                        sanitizedMetadata[k] = "[REDACTED]";
                    }
                    continue;
                }
            }

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
