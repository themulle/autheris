using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors;

/// <summary>
/// Centralized row masking and column stripping utility for connectors, streaming pipelines, and federation engines.
/// </summary>
public static class ConnectorRowMasker
{
    public static Dictionary<string, object?> MaskRow(
        IReadOnlyDictionary<string, object?> rawRow,
        TableMetadata metadata,
        TableAccessDecision decision,
        IColumnMaskingProvider maskingProvider,
        string? tenantId = null,
        string? defaultHmacKeyId = null)
    {
        ArgumentNullException.ThrowIfNull(rawRow);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(maskingProvider);

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in metadata.Columns)
        {
            var access = decision.GetColumnAccess(col.ColumnName);
            if (access == ColumnAccessLevel.Deny)
            {
                continue; // Strip denied columns completely
            }

            // Zero-Trust: Sensitive columns in catalog never output cleartext without explicit Clear rule
            bool isSensitiveInCatalog = col.IsSensitive || metadata.ColumnMaskingRules.ContainsKey(col.ColumnName);
            if (isSensitiveInCatalog && !decision.HasExplicitClear(col.ColumnName))
            {
                access = ColumnAccessLevel.Mask;
            }

            if (rawRow.TryGetValue(col.ColumnName, out var rawVal))
            {
                if (access == ColumnAccessLevel.Mask)
                {
                    var rule = metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule)
                        ? mRule
                        : new MaskingRule { RuleType = "REDACT" };
                    // SEC D-3: HMAC pseudonyms are tenant-scoped (idempotent - values already scoped by the SQL path stay single-scoped).
                    rule = Autheris.Application.Services.GatewayExecutionService.ScopeRuleForTenant(rule, tenantId, defaultHmacKeyId);
                    rawVal = maskingProvider.MaskValue(col.ColumnName, rawVal, rule);
                }
                dict[col.ColumnName] = rawVal;
            }
        }
        return dict;
    }
}
