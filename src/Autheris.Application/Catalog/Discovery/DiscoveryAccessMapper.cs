namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Pure mapper that projects a <see cref="TableMetadata"/>, PDP <see cref="TableAccessDecision"/>,
/// active owners, status hints, and optional artifacts into a sanitized <see cref="DiscoveryObject"/>.
/// Never reads consents directly and never discloses row-filter predicates or parameter values.
/// </summary>
public static class DiscoveryAccessMapper
{
    public static DiscoveryObject Map(
        TableMetadata table,
        TableAccessDecision decision,
        IReadOnlyList<DataOwner> activeOwners,
        bool isPendingRequest,
        bool isRecentlyExpired,
        DiscoveryArtifacts? artifacts,
        string? language,
        bool isHidden)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(activeOwners);

        var (accessState, rowScope) = MapAccessAndRowScope(decision, isPendingRequest, isRecentlyExpired);
        var (tableStats, colStatsByName) = ParseStatistics(artifacts);
        var visibleCols = table.Columns.Where(static c => !c.DiscoveryHidden).ToList();
        var visibleColNames = new HashSet<string>(visibleCols.Select(static c => c.ColumnName), StringComparer.OrdinalIgnoreCase);

        if (tableStats?.PartitionColumns is { Count: > 0 } partCols)
        {
            var filteredPartitions = partCols.Where(visibleColNames.Contains).ToList();
            tableStats = tableStats with { PartitionColumns = filteredPartitions };
        }

        var mappedCols = new List<DiscoveryColumn>(visibleCols.Count);
        foreach (var col in visibleCols)
        {
            var (colState, clearInSomeRows) = MapColumnState(table, decision, col.ColumnName);
            var pii = col.IsSensitive || CatalogPiiDetector.IsPii(col.ColumnName, col.IsSensitive);
            var colLocalized = !string.IsNullOrWhiteSpace(language) ? col.Texts.Resolve(language) : null;
            var colDisplayName = DiscoveryTextSanitizer.SanitizeText(
                colLocalized?.Title ?? col.DisplayName,
                DiscoveryTextSanitizer.MaxDisplayNameRunes);
            var colDescription = DiscoveryTextSanitizer.SanitizeText(
                colLocalized?.Description ?? col.Description,
                DiscoveryTextSanitizer.MaxDescriptionRunes);

            DiscoveryColumnStatistics? stats = null;
            if (colState != DiscoveryColumnState.NoAccess &&
                colStatsByName != null &&
                colStatsByName.TryGetValue(col.ColumnName, out var parsedColStats))
            {
                stats = parsedColStats;
            }

            mappedCols.Add(new DiscoveryColumn(
                Name: col.ColumnName,
                DisplayName: colDisplayName,
                DataType: col.DataType,
                Description: colDescription,
                Pii: pii,
                ClearAccessRequiresSeparateApproval: pii,
                State: colState,
                Statistics: stats,
                ClearInSomeRows: clearInSomeRows));
        }

        var tableLocalized = !string.IsNullOrWhiteSpace(language) ? table.Table.Texts.Resolve(language) : null;
        var tableDisplayName = DiscoveryTextSanitizer.SanitizeText(
            tableLocalized?.Title ?? table.Table.DisplayName,
            DiscoveryTextSanitizer.MaxDisplayNameRunes);
        var tableDescription = DiscoveryTextSanitizer.SanitizeText(
            tableLocalized?.Description ?? table.Table.Description,
            DiscoveryTextSanitizer.MaxDescriptionRunes);
        var tags = DiscoveryTextSanitizer.SanitizeTags(table.CatalogTags);

        var owners = activeOwners
            .Where(static o => o.IsActive)
            .Select(static o => new DiscoveryOwner(
                DisplayName: DiscoveryTextSanitizer.SanitizeRequiredText(o.DisplayName, string.Empty),
                Email: string.IsNullOrWhiteSpace(o.Email) ? null : DiscoveryTextSanitizer.SanitizeText(o.Email, DiscoveryTextSanitizer.MaxDisplayNameRunes)))
            .ToList();

        var samplesAvailable = !table.Table.SamplesDisabled && HasSamples(artifacts?.SamplesJson);

        return new DiscoveryObject(
            Kind: table.Table.ObjectKind,
            Domain: table.Identifier.Domain,
            Schema: table.Identifier.Schema,
            Name: table.Identifier.TableName,
            DisplayName: string.IsNullOrEmpty(tableDisplayName) ? null : tableDisplayName,
            Description: tableDescription,
            Sensitivity: table.Table.Sensitivity,
            Tags: tags,
            Owners: owners,
            Access: accessState,
            RowScope: rowScope,
            Columns: mappedCols,
            Statistics: tableStats,
            SamplesAvailable: samplesAvailable,
            IsHidden: isHidden);
    }

    private static (DiscoveryAccessState Access, DiscoveryRowScope? RowScope) MapAccessAndRowScope(
        TableAccessDecision decision,
        bool isPendingRequest,
        bool isRecentlyExpired)
    {
        if (decision.IsAllowed)
        {
            var restricted = HasRowPredicate(decision);
            return (DiscoveryAccessState.Granted, restricted ? DiscoveryRowScope.RowRestricted : DiscoveryRowScope.Full);
        }

        if (isPendingRequest)
        {
            return (DiscoveryAccessState.Requested, null);
        }

        if (isRecentlyExpired)
        {
            return (DiscoveryAccessState.Expired, null);
        }

        return (DiscoveryAccessState.None, null);
    }

    private static bool HasRowPredicate(TableAccessDecision decision) =>
        decision.RowPolicy is not null ||
        decision.MandatoryRowPolicy is not null ||
        decision.ConsentFilterRecipe is not null ||
        !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
        !string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql);

    private static (DiscoveryColumnState State, bool ClearInSomeRows) MapColumnState(
        TableMetadata table,
        TableAccessDecision decision,
        string columnName)
    {
        if (!decision.IsAllowed)
        {
            return (DiscoveryColumnState.NoAccess, false);
        }

        if (decision.RowDependentColumns is { Count: > 0 } rd && rd.Contains(columnName))
        {
            return (DiscoveryColumnState.Masked, true);
        }

        return decision.GetEffectiveColumnAccess(columnName, table) switch
        {
            ColumnAccessLevel.Clear => (DiscoveryColumnState.Clear, false),
            ColumnAccessLevel.Mask => (DiscoveryColumnState.Masked, false),
            _ => (DiscoveryColumnState.NoAccess, false)
        };
    }

    private static bool HasSamples(string? samplesJson)
    {
        if (string.IsNullOrWhiteSpace(samplesJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(samplesJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (DiscoveryTableStatistics? TableStats, Dictionary<string, DiscoveryColumnStatistics>? ColumnStats) ParseStatistics(
        DiscoveryArtifacts? artifacts)
    {
        if (artifacts is null || string.IsNullOrWhiteSpace(artifacts.StatisticsJson))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(artifacts.StatisticsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            DiscoveryTableStatistics? tableStats = null;
            if (TryGetPropertyIgnoreCase(root, "table", out var tbl) && tbl.ValueKind == JsonValueKind.Object)
            {
                var partCols = ReadStringList(tbl, "partitionColumns");
                tableStats = new DiscoveryTableStatistics(
                    ApproxRowCount: ReadNullableInt64(tbl, "approxRowCount"),
                    SizeBytes: ReadNullableInt64(tbl, "sizeBytes"),
                    LastUpdatedAt: ReadNullableDateTimeOffset(tbl, "lastUpdatedAt"),
                    PartitionColumns: partCols,
                    UploadedAt: artifacts.UploadedAt);
            }

            Dictionary<string, DiscoveryColumnStatistics>? colStats = null;
            if (TryGetPropertyIgnoreCase(root, "columns", out var cols) && cols.ValueKind == JsonValueKind.Object)
            {
                colStats = new Dictionary<string, DiscoveryColumnStatistics>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in cols.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var c = prop.Value;
                    colStats[prop.Name] = new DiscoveryColumnStatistics(
                        Nullable: ReadNullableBool(c, "nullable"),
                        NullRatio: ReadNullableDouble(c, "nullRatio"),
                        ApproxDistinctCount: ReadNullableInt64(c, "approxDistinctCount"),
                        MinLength: ReadNullableInt32(c, "minLength"),
                        MaxLength: ReadNullableInt32(c, "maxLength"),
                        MinValue: ReadSanitizedScalar(c, "minValue"),
                        MaxValue: ReadSanitizedScalar(c, "maxValue"),
                        TopValues: ReadStringList(c, "topValues"));
                }
            }

            return (tableStats, colStats);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static long? ReadNullableInt64(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int? ReadNullableInt32(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static double? ReadNullableDouble(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;

    private static bool? ReadNullableBool(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static DateTimeOffset? ReadNullableDateTimeOffset(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : null;

    private static string? ReadSanitizedScalar(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var v))
        {
            return null;
        }

        var raw = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

        return raw is null ? null : DiscoveryTextSanitizer.SanitizeText(raw, 1024);
    }

    private static IReadOnlyList<string>? ReadStringList(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            var raw = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            var clean = raw is null ? null : DiscoveryTextSanitizer.SanitizeText(raw, 200);
            if (!string.IsNullOrEmpty(clean))
            {
                list.Add(clean);
            }
        }

        return list;
    }
}
