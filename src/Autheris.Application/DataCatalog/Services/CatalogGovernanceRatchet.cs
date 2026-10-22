namespace Autheris.Application.DataCatalog.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>
/// SEC M-32: Merge semantics for catalog / metadata mirror syncs. Governance-relevant flags may only be tightened:
/// four-eyes, sensitivity, column sensitivity and masking rules are never weakened, a deactivated table stays inactive,
/// and data source routing fields (DataSourceType, HttpEndpoint, PluginName, SourceType, SourceName) unknown to catalogs
/// are preserved.
/// Columns missing from the incoming snapshot keep their persisted protection.
/// SEC-RATCHET-01: the result is derived from the persisted table, columns and metadata, so properties a catalog does not
/// own (e.g. required filter columns, enforcement mode, physical database name, localized texts) are never wiped by a sync.
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

        int? mergedMinGroupSize = exTable.MinGroupSize.HasValue && inTable.MinGroupSize.HasValue
            ? Math.Max(exTable.MinGroupSize.Value, inTable.MinGroupSize.Value)
            : exTable.MinGroupSize ?? inTable.MinGroupSize;

        // SEC-RATCHET-01: the merge starts from the persisted table so every property a catalog does not own (required filter
        // columns / groups, enforcement mode, physical database, location, row filter, group size, tags, visibility, expected
        // rows, routing fields) survives the sync. Only catalog-owned descriptive fields and the ratcheted governance flags are
        // overridden. Routing fields (DataSourceType, HttpEndpoint, PluginName) are never overridden.
        var table = exTable with
        {
            Id = tableId,
            SourceType = !string.IsNullOrWhiteSpace(exTable.SourceType) ? exTable.SourceType : inTable.SourceType,
            SourceName = RatchetSourceName(exTable.SourceName, inTable.SourceName, existing.Identifier.Domain),
            SchemaName = inTable.SchemaName,
            TableName = inTable.TableName,
            DisplayName = !string.IsNullOrWhiteSpace(inTable.DisplayName) ? inTable.DisplayName : exTable.DisplayName,
            Description = inTable.Description ?? exTable.Description,
            LongDescription = inTable.LongDescription ?? exTable.LongDescription,
            Texts = inTable.Texts is { IsEmpty: false } ? inTable.Texts : exTable.Texts,
            DocumentationSource = inTable.DocumentationSource ?? exTable.DocumentationSource,
            Sensitivity = StricterSensitivity(exTable.Sensitivity, inTable.Sensitivity),
            RequiresFourEyes = exTable.RequiresFourEyes || inTable.RequiresFourEyes,
            IsActive = exTable.IsActive && inTable.IsActive,
            DataSourceType = exTable.DataSourceType != DataSourceType.Sql ? exTable.DataSourceType : inTable.DataSourceType,
            EnforcementMode = exTable.EnforcementMode != Autheris.Domain.Options.EnforcementMode.AutherisEnforced ? exTable.EnforcementMode : inTable.EnforcementMode,
            PhysicalDatabaseName = !string.IsNullOrWhiteSpace(exTable.PhysicalDatabaseName) ? exTable.PhysicalDatabaseName : inTable.PhysicalDatabaseName,
            Location = !string.IsNullOrWhiteSpace(exTable.Location) ? exTable.Location : inTable.Location,
            HttpEndpoint = exTable.HttpEndpoint ?? inTable.HttpEndpoint,
            PluginName = !string.IsNullOrWhiteSpace(exTable.PluginName) ? exTable.PluginName : inTable.PluginName,
            Tags = UnionTags(exTable.Tags, inTable.Tags),
            Visibility = exTable.Visibility != DatasetVisibility.Hidden ? exTable.Visibility : inTable.Visibility,
            RlsExpression = !string.IsNullOrWhiteSpace(exTable.RlsExpression) ? exTable.RlsExpression : inTable.RlsExpression,
            MinGroupSize = mergedMinGroupSize,
            SmallGroupStrategy = exTable.SmallGroupStrategy ?? inTable.SmallGroupStrategy,
            RequiredFilterColumns = UnionRequiredFilterColumns(exTable.RequiredFilterColumns, inTable.RequiredFilterColumns),
            RequiredFilterColumnGroups = UnionRequiredFilterColumnGroups(exTable.RequiredFilterColumnGroups, inTable.RequiredFilterColumnGroups),
            ExpectedRows = inTable.ExpectedRows ?? exTable.ExpectedRows
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

            // SEC-RATCHET-01: TableColumn is a class (no `with`), so every property is listed; persisted values that the
            // catalog does not deliver (display name, localized texts, ...) are kept.
            mergedColumns.Add(new TableColumn
            {
                Id = exCol?.Id ?? col.Id,
                TableId = tableId,
                ColumnName = col.ColumnName,
                DisplayName = !string.IsNullOrWhiteSpace(col.DisplayName) ? col.DisplayName : exCol?.DisplayName,
                DataType = string.IsNullOrWhiteSpace(col.DataType) && exCol != null ? exCol.DataType : col.DataType,
                IsSensitive = col.IsSensitive || exCol?.IsSensitive == true,
                Description = col.Description ?? exCol?.Description,
                LongDescription = col.LongDescription ?? exCol?.LongDescription,
                Texts = col.Texts is { IsEmpty: false } ? col.Texts : (exCol?.Texts ?? LocalizedTexts.Empty),
                DocumentationSource = col.DocumentationSource ?? exCol?.DocumentationSource,
                Meta = col.Meta is { Count: > 0 } ? col.Meta : (exCol?.Meta ?? col.Meta)
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

        // SEC-94-11: the incoming definition (and the replace intent) is carried only when it asks to replace; otherwise the stored
        // definition is kept and the result stays unflagged, so a round trip can never wipe it.
        var replace = incoming.ReplaceKeyDefinition;

        // SEC-RATCHET-01: metadata-level settings a catalog does not own (owner tenant, profile release, synthetic samples,
        // row filter evaluation, group size overrides, row count hints) are carried over from the persisted state as well.
        return existing with
        {
            Table = table,
            Identifier = incoming.Identifier,
            OwnerTenantId = existing.OwnerTenantId,
            Columns = mergedColumns,
            ColumnMaskingRules = mergedRules,
            PrimaryKeyColumns = replace ? incoming.PrimaryKeyColumns : existing.PrimaryKeyColumns,
            Indexes = replace ? incoming.Indexes : existing.Indexes,
            RowFilterEvaluation = existing.RowFilterEvaluation,
            CatalogView = existing.CatalogView,
            CatalogTags = UnionTags(existing.CatalogTags, incoming.CatalogTags),
            ReplaceKeyDefinition = replace,
            ReleaseProfileToCatalogViewers = existing.ReleaseProfileToCatalogViewers,
            ProfilePublicationPolicy = existing.ProfilePublicationPolicy,
            PredefinedSyntheticSamples = existing.PredefinedSyntheticSamples,
            EstimatedRowCount = incoming.EstimatedRowCount ?? existing.EstimatedRowCount,
            ExpectedRows = incoming.ExpectedRows ?? existing.ExpectedRows,
            MinGroupSize = existing.MinGroupSize,
            SmallGroupStrategy = existing.SmallGroupStrategy,
            DeletedKeyPolicy = existing.DeletedKeyPolicy
        };
    }

    public static ImmutableArray<string> UnionRequiredFilterColumns(ImmutableArray<string> stored, ImmutableArray<string> incoming)
    {
        var s = stored.IsDefault ? ImmutableArray<string>.Empty : stored;
        var inc = incoming.IsDefault ? ImmutableArray<string>.Empty : incoming;
        if (s.IsEmpty && inc.IsEmpty) return ImmutableArray<string>.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var col in s)
        {
            if (!string.IsNullOrWhiteSpace(col) && seen.Add(col))
            {
                builder.Add(col);
            }
        }
        foreach (var col in inc)
        {
            if (!string.IsNullOrWhiteSpace(col) && seen.Add(col))
            {
                builder.Add(col);
            }
        }
        return builder.ToImmutable();
    }

    public static ImmutableArray<ImmutableArray<string>> UnionRequiredFilterColumnGroups(
        ImmutableArray<ImmutableArray<string>> stored,
        ImmutableArray<ImmutableArray<string>> incoming)
    {
        var s = stored.IsDefault ? ImmutableArray<ImmutableArray<string>>.Empty : stored;
        var inc = incoming.IsDefault ? ImmutableArray<ImmutableArray<string>>.Empty : incoming;
        if (s.IsEmpty && inc.IsEmpty) return ImmutableArray<ImmutableArray<string>>.Empty;
        if (s.IsEmpty) return inc;
        if (inc.IsEmpty) return s;

        var builder = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var seenKeySets = new List<HashSet<string>>();

        foreach (var g in s)
        {
            if (g.IsDefaultOrEmpty) continue;
            var set = new HashSet<string>(g, StringComparer.OrdinalIgnoreCase);
            if (set.Count > 0 && !seenKeySets.Any(existing => existing.SetEquals(set)))
            {
                seenKeySets.Add(set);
                builder.Add(g);
            }
        }

        foreach (var g in inc)
        {
            if (g.IsDefaultOrEmpty) continue;
            var set = new HashSet<string>(g, StringComparer.OrdinalIgnoreCase);
            if (set.Count > 0 && !seenKeySets.Any(existing => existing.SetEquals(set)))
            {
                seenKeySets.Add(set);
                builder.Add(g);
            }
        }

        return builder.ToImmutable();
    }

    public static IReadOnlyList<string> UnionTags(IEnumerable<string>? existing, IEnumerable<string>? incoming)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing != null)
        {
            foreach (var t in existing)
            {
                if (!string.IsNullOrWhiteSpace(t) && seen.Add(t))
                    result.Add(t);
            }
        }
        if (incoming != null)
        {
            foreach (var t in incoming)
            {
                if (!string.IsNullOrWhiteSpace(t) && seen.Add(t))
                    result.Add(t);
            }
        }
        return result;
    }

    /// <summary>
    /// EXT-06: Disclosure-based masking relaxation check.
    /// Returns false if incoming tier is higher (strictly tighter);
    /// returns true if lower (relaxation);
    /// at equal tier false only when RuleType is equal and every disclosure parameter is equal or stricter:
    /// KeepPrefix/KeepSuffix/FixedLength-revealed chars not larger, Decimals not larger, RadiusMeters not smaller,
    /// and PatternOrFormat, Replacement, MaskChar, HmacKeyId, Mode, TokenDomain equal.
    /// Everything else (incl. type change inside a tier) is a relaxation.
    /// </summary>
    public static bool IsMaskingRelaxation(MaskingRule stored, MaskingRule incoming)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(incoming);

        var storedTier = MaskingRuleStrength(stored);
        var incomingTier = MaskingRuleStrength(incoming);

        if (incomingTier > storedTier)
        {
            return false;
        }

        if (incomingTier < storedTier)
        {
            return true;
        }

        // Equal tier: rule type must be equal
        if (!string.Equals(stored.RuleType, incoming.RuleType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // KeepPrefix: not larger
        if ((incoming.KeepPrefix ?? 0) > (stored.KeepPrefix ?? 0))
        {
            return true;
        }

        // KeepSuffix: not larger
        if ((incoming.KeepSuffix ?? 0) > (stored.KeepSuffix ?? 0))
        {
            return true;
        }

        // FixedLength: if stored hides length (true), incoming must not reveal length (false/null)
        if (stored.FixedLength == true && incoming.FixedLength != true)
        {
            return true;
        }

        // Decimals: not larger
        if ((incoming.Decimals ?? 0) > (stored.Decimals ?? 0))
        {
            return true;
        }

        // RadiusMeters: not smaller (larger radius = more noise = stricter privacy)
        if ((incoming.RadiusMeters ?? 0.0) < (stored.RadiusMeters ?? 0.0))
        {
            return true;
        }

        // Parameters that must be equal
        if (!string.Equals(stored.PatternOrFormat ?? string.Empty, incoming.PatternOrFormat ?? string.Empty, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(stored.Replacement ?? string.Empty, incoming.Replacement ?? string.Empty, StringComparison.Ordinal))
        {
            return true;
        }

        if (stored.MaskChar != incoming.MaskChar)
        {
            return true;
        }

        if (!string.Equals(stored.HmacKeyId ?? string.Empty, incoming.HmacKeyId ?? string.Empty, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(stored.Mode ?? string.Empty, incoming.Mode ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(stored.TokenDomain ?? string.Empty, incoming.TokenDomain ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// EXT-2: SourceName selects the database connection (SqlTableReadExecutor, procedures, plugins). A sync never
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
