namespace Autheris.Application.DataCatalog.Services;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// SEC M-32: Merge semantics for catalog / metadata mirror syncs. Governance-relevant flags may only be tightened:
/// four-eyes, sensitivity, column sensitivity and masking rules are never weakened, a deactivated table stays inactive,
/// and data source routing fields (DataSourceType, HttpEndpoint, PluginName, SourceType, SourceName) unknown to catalogs
/// are preserved.
/// Columns missing from the incoming snapshot keep their persisted protection.
/// </summary>
public static class CatalogGovernanceRatchet
{
    public static TableMetadata Merge(TableMetadata incoming, TableMetadata? existing)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (existing == null)
        {
            return incoming;
        }

        var inTable = incoming.Table;
        var exTable = existing.Table;
        var tableId = exTable.Id != Guid.Empty ? exTable.Id : inTable.Id;

        var table = new Table
        {
            Id = tableId,
            SourceType = !string.IsNullOrWhiteSpace(exTable.SourceType) ? exTable.SourceType : inTable.SourceType,
            SourceName = RatchetSourceName(exTable.SourceName, inTable.SourceName, existing.Identifier.Domain),
            SchemaName = inTable.SchemaName,
            TableName = inTable.TableName,
            DisplayName = !string.IsNullOrWhiteSpace(inTable.DisplayName) ? inTable.DisplayName : exTable.DisplayName,
            Description = inTable.Description ?? exTable.Description,
            LongDescription = inTable.LongDescription ?? exTable.LongDescription,
            DocumentationSource = inTable.DocumentationSource ?? exTable.DocumentationSource,
            Sensitivity = StricterSensitivity(exTable.Sensitivity, inTable.Sensitivity),
            RequiresFourEyes = exTable.RequiresFourEyes || inTable.RequiresFourEyes,
            IsActive = exTable.IsActive && inTable.IsActive,
            DataSourceType = exTable.DataSourceType,
            HttpEndpoint = exTable.HttpEndpoint,
            PluginName = exTable.PluginName
        };

        var existingColumns = new Dictionary<string, TableColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in existing.Columns)
        {
            existingColumns.TryAdd(col.ColumnName, col);
        }

        var mergedColumns = new List<TableColumn>(Math.Max(incoming.Columns.Count, existing.Columns.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in incoming.Columns)
        {
            if (!seen.Add(col.ColumnName))
            {
                continue;
            }

            existingColumns.TryGetValue(col.ColumnName, out var exCol);
            mergedColumns.Add(new TableColumn
            {
                Id = exCol?.Id ?? col.Id,
                TableId = tableId,
                ColumnName = col.ColumnName,
                DataType = string.IsNullOrWhiteSpace(col.DataType) && exCol != null ? exCol.DataType : col.DataType,
                IsSensitive = col.IsSensitive || exCol?.IsSensitive == true,
                Description = col.Description ?? exCol?.Description,
                LongDescription = col.LongDescription ?? exCol?.LongDescription,
                DocumentationSource = col.DocumentationSource ?? exCol?.DocumentationSource,
                Meta = col.Meta.Count > 0 ? col.Meta : (exCol?.Meta ?? col.Meta)
            });
        }

        foreach (var exCol in existing.Columns)
        {
            if (seen.Add(exCol.ColumnName))
            {
                // Column vanished from the catalog snapshot: keep it (and its protection) unchanged.
                mergedColumns.Add(exCol);
            }
        }

        var mergedRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var (colName, rule) in existing.ColumnMaskingRules)
        {
            mergedRules[colName] = rule;
        }

        foreach (var (colName, rule) in incoming.ColumnMaskingRules)
        {
            if (!mergedRules.TryGetValue(colName, out var current) || MaskingRuleStrength(rule) > MaskingRuleStrength(current))
            {
                mergedRules[colName] = rule;
            }
        }

        return new TableMetadata
        {
            Table = table,
            Identifier = incoming.Identifier,
            Columns = mergedColumns,
            ColumnMaskingRules = mergedRules,
            PrimaryKeyColumns = existing.PrimaryKeyColumns
        };
    }

    /// <summary>
    /// EXT-2: SourceName selects the database connection (SqlDataSourceExecutor, procedures, plugins). A sync never
    /// moves a table to another connection: a persisted SourceName is kept, and an unbound table (which runs on the
    /// connection of its domain) may only be bound to exactly that domain.
    /// </summary>
    private static string RatchetSourceName(string? existing, string? incoming, string domain)
    {
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        return !string.IsNullOrWhiteSpace(incoming) && string.Equals(incoming, domain, StringComparison.OrdinalIgnoreCase)
            ? incoming
            : existing ?? string.Empty;
    }

    public static string StricterSensitivity(string? existing, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(existing)) return string.IsNullOrWhiteSpace(incoming) ? "NORMAL" : incoming;
        if (string.IsNullOrWhiteSpace(incoming)) return existing;
        return SensitivityRank(incoming) > SensitivityRank(existing) ? incoming : existing;
    }

    // D-4: one ranking for the ratchet and Table.IsSensitivityHigh (unknown classifications rank like HIGH).
    private static int SensitivityRank(string sensitivity) => Table.SensitivityRank(sensitivity);

    public static int MaskingRuleStrength(MaskingRule rule) => (rule.RuleType ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "NULLIFY" => 4,
        "REDACT" => 3,
        "HMAC" or "HMAC_SHA256" or "TOKENIZE" or "TOKENIZATION" => 2,
        _ => 1
    };
}
