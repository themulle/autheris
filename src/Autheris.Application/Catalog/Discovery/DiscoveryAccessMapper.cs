namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

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
        bool isHidden,
        TableAccessDecision? insertDecision = null,
        TableAccessDecision? updateDecision = null,
        TableAccessDecision? deleteDecision = null,
        IReadOnlyDictionary<string, DataClassification>? classificationDefinitions = null,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>>? repoColumnClassifications = null,
        string? tenantId = null,
        bool hideMaskedTextForRestrictedScopes = false,
        EffectiveProtection? effectiveProtection = null,
        bool importSamplesAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(activeOwners);

        var (accessState, rowScope) = MapAccessAndRowScope(decision, isPendingRequest, isRecentlyExpired);
        var (tableStats, colStatsByName) = artifacts is { Provenance: not ArtifactProvenance.Import }
            ? ParseStatistics(artifacts)
            : (null, null);
        var visibleCols = table.Columns.Where(static c => !c.DiscoveryHidden).ToList();

        var defs = classificationDefinitions
            ?? ClassificationAccessResolver.BuiltInCatalog(tenantId ?? ClassificationScopes.Global).Definitions;
        var mappedCols = new List<DiscoveryColumn>(visibleCols.Count);
        HashSet<string>? hiddenTextColumns = null;
        foreach (var col in visibleCols)
        {
            var (colState, clearInSomeRows) = MapColumnState(table, decision, col.ColumnName);
            bool hideText = colState != DiscoveryColumnState.Clear &&
                (decision.IsCatalogTextHidden(col.ColumnName, table) ||
                 (hideMaskedTextForRestrictedScopes && decision.ScopeClearance is not null));
            IReadOnlyList<ColumnClassification>? overrideRows = null;
            repoColumnClassifications?.TryGetValue(col.ColumnName, out overrideRows);
            var (pii, clearSep, classifications, clearRequestable) = ResolveColumnClassifications(
                table, col, isHidden, tenantId, defs, overrideRows);
            var colLocalized = !hideText && !string.IsNullOrWhiteSpace(language) ? col.Texts.Resolve(language) : null;
            var colDisplayName = hideText ? null : DiscoveryTextSanitizer.SanitizeText(
                colLocalized?.Title ?? col.DisplayName,
                DiscoveryTextSanitizer.MaxDisplayNameRunes);
            var colDescription = hideText ? null : DiscoveryTextSanitizer.SanitizeText(
                colLocalized?.Description ?? col.Description,
                DiscoveryTextSanitizer.MaxDescriptionRunes);

            var colLongDescription = DiscoveryTextSanitizer.SanitizeText(
                colLocalized?.LongDescription ?? col.LongDescription,
                DiscoveryTextSanitizer.MaxDescriptionRunes * 4,
                allowNewlinesAndTabs: true);

            DiscoveryColumnStatistics? stats = null;
            if (!hideText &&
                colState != DiscoveryColumnState.NoAccess &&
                colStatsByName != null &&
                colStatsByName.TryGetValue(col.ColumnName, out var parsedColStats))
            {
                // Restricted-scope callers only receive value-bearing statistics for columns whose effective state is Clear.
                stats = decision.ScopeClearance is not null && colState != DiscoveryColumnState.Clear
                    ? parsedColStats with { MinValue = null, MaxValue = null, TopValues = null }
                    : parsedColStats;
            }

            mappedCols.Add(new DiscoveryColumn(
                Name: col.ColumnName,
                DisplayName: colDisplayName,
                DataType: col.DataType,
                Description: colDescription,
                Pii: pii,
                ClearAccessRequiresSeparateApproval: clearSep,
                State: colState,
                Statistics: stats,
                ClearInSomeRows: clearInSomeRows,
                WriteProtected: col.WriteProtected,
                Classifications: classifications,
                ClearRequestable: clearRequestable,
                LongDescription: hideText ? null : colLongDescription,
                Role: col.Role));
            if (hideText)
            {
                (hiddenTextColumns ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(col.ColumnName);
            }
        }

        tableStats = FilterPartitionColumns(tableStats, mappedCols);

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

        var samplesAvailable = !table.Table.SamplesDisabled && (
            importSamplesAvailable ||
            (decision.IsAllowed &&
             artifacts is { Provenance: not ArtifactProvenance.Import } &&
             HasSamples(artifacts.SamplesJson) &&
             mappedCols.Any(c => decision.ScopeClearance is not null
                 ? c.State == DiscoveryColumnState.Clear
                 : c.State != DiscoveryColumnState.NoAccess)));
        var writeState = MapWriteState(table, visibleCols, insertDecision, updateDecision, deleteDecision);

        return new DiscoveryObject(
            Kind: table.Table.ObjectKind,
            Domain: table.Identifier.Domain,
            Schema: table.Identifier.Schema,
            Name: table.Identifier.TableName,
            DisplayName: string.IsNullOrEmpty(tableDisplayName) ? null : tableDisplayName,
            Description: tableDescription,
            Sensitivity: (effectiveProtection?.TableClass ?? ProtectionClass.Confidential).ToCanonicalName(),
            Tags: tags,
            Owners: owners,
            Access: accessState,
            RowScope: rowScope,
            Columns: mappedCols,
            Statistics: tableStats,
            SamplesAvailable: samplesAvailable,
            IsHidden: isHidden,
            Write: writeState,
            QueryShape: MapQueryShape(table, mappedCols))
        {
            HiddenTextColumns = hiddenTextColumns ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    // Partition columns are named in the table statistics: only a column the caller can see may be named (none for an ungranted object).
    private static DiscoveryTableStatistics? FilterPartitionColumns(DiscoveryTableStatistics? stats, IReadOnlyList<DiscoveryColumn> columns)
    {
        if (stats?.PartitionColumns is not { Count: > 0 } partCols)
        {
            return stats;
        }

        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (DiscoveryColumnVisibility.IsVisible(column.State))
            {
                visible.Add(column.Name);
            }
        }

        return stats with { PartitionColumns = partCols.Where(visible.Contains).ToList() };
    }

    /// <summary>
    /// Issues #805/#807: keys and indexes over the listed columns whose state is <see cref="DiscoveryColumnState.Clear"/>
    /// (the shared <see cref="DatasetIndexProjection"/> rule), required filters over the listed columns the caller has any access to.
    /// <paramref name="columns"/> holds only columns that are not discovery-hidden; a caller without access to any column gets nothing.
    /// </summary>
    private static DiscoveryQueryShape MapQueryShape(TableMetadata table, IReadOnlyList<DiscoveryColumn> columns)
    {
        // Runs for every listed object: a set is built only when the table declares something it is needed for.
        var hasKeys = table.PrimaryKeyColumns.Count > 0 || table.Indexes.Count > 0;
        var hasFilters = table.RequiredFilterColumns.Length > 0 || table.RequiredFilterColumnGroups.Length > 0;
        if (!hasKeys && !hasFilters)
        {
            return DiscoveryQueryShape.Empty;
        }

        HashSet<string>? listed = null;
        HashSet<string>? clear = null;
        foreach (var column in columns)
        {
            if (hasFilters && DiscoveryColumnVisibility.IsVisible(column.State))
            {
                (listed ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(column.Name);
            }

            if (hasKeys && column.State == DiscoveryColumnState.Clear)
            {
                (clear ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(column.Name);
            }
        }

        if (listed is null && clear is null)
        {
            return DiscoveryQueryShape.Empty;
        }

        var keys = clear is null ? DatasetKeyProjection.Empty : DatasetIndexProjection.Project(table, clear);
        IReadOnlyList<string> requiredColumns = [];
        IReadOnlyList<IReadOnlyList<string>> requiredGroups = [];
        if (listed is not null)
        {
            (requiredColumns, requiredGroups) = RequiredFilterColumnProjection.Project(table, listed);
        }

        return new DiscoveryQueryShape(keys.PrimaryKeyColumns, keys.PrimaryKeyDirections, keys.Indexes, requiredColumns, requiredGroups);
    }

    private static DiscoveryWriteState MapWriteState(
        TableMetadata table,
        IReadOnlyList<TableColumn> visibleCols,
        TableAccessDecision? insertDecision,
        TableAccessDecision? updateDecision,
        TableAccessDecision? deleteDecision)
    {
        if (insertDecision is null && updateDecision is null && deleteDecision is null)
        {
            return DiscoveryWriteState.None;
        }

        var visibleNonProtected = visibleCols
            .Where(static c => !c.WriteProtected)
            .Select(static c => c.ColumnName)
            .ToList();
        var pkSet = table.PrimaryKeyColumns.Count > 0
            ? new HashSet<string>(table.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase)
            : null;

        IReadOnlyList<string> insertColumns = [];
        if (insertDecision is { IsAllowed: true, WriteGrant: { } insertGrant } && insertGrant.WritableColumns.Count > 0)
        {
            var writable = new HashSet<string>(insertGrant.WritableColumns, StringComparer.OrdinalIgnoreCase);
            insertColumns = visibleNonProtected.Where(writable.Contains).ToList();
        }

        IReadOnlyList<string> updateColumns = [];
        if (updateDecision is { IsAllowed: true, WriteGrant: { } updateGrant } && updateGrant.WritableColumns.Count > 0)
        {
            var writable = new HashSet<string>(updateGrant.WritableColumns, StringComparer.OrdinalIgnoreCase);
            updateColumns = visibleNonProtected
                .Where(c => writable.Contains(c) && (pkSet == null || !pkSet.Contains(c)))
                .ToList();
        }

        var canInsert = insertDecision is { IsAllowed: true, WriteGrant: not null } && insertColumns.Count > 0;
        var canUpdate = updateDecision is { IsAllowed: true, WriteGrant: not null } && updateColumns.Count > 0;
        var canDelete = deleteDecision is { IsAllowed: true, WriteGrant: not null };
        var updateRowScope = canUpdate ? updateDecision!.WriteGrant!.RowScope : (WriteRowScope?)null;
        var requestable = IsWriteRequestable(table, visibleNonProtected, insertDecision, updateDecision);

        return new DiscoveryWriteState(
            CanInsert: canInsert,
            CanUpdate: canUpdate,
            CanDelete: canDelete,
            UpdateRowScope: updateRowScope,
            InsertColumns: insertColumns,
            UpdateColumns: updateColumns,
            Requestable: requestable);
    }

    private static bool IsWriteRequestable(
        TableMetadata table,
        IReadOnlyList<string> visibleNonProtected,
        TableAccessDecision? insertDecision,
        TableAccessDecision? updateDecision)
    {
        if (!table.Table.IsActive ||
            table.Table.ObjectKind == DiscoveryObjectKind.Procedure ||
            visibleNonProtected.Count == 0 ||
            string.Equals(table.Table.DocumentationSource, SemanticVirtualTables.SourceMarker, StringComparison.Ordinal) ||
            string.Equals(table.Identifier.Domain, SemanticVirtualTables.Domain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsBlockedForWriteRequest(insertDecision) && !IsBlockedForWriteRequest(updateDecision);
    }

    private static bool IsBlockedForWriteRequest(TableAccessDecision? decision)
    {
        if (decision is null || decision.IsAllowed)
        {
            return false;
        }

        if (string.Equals(decision.WriteGrant?.DenyCode, "write_grant_policy_dependency", StringComparison.Ordinal))
        {
            return true;
        }

        return decision.Reasons.Any(static r => r.Code is
            "write_grant_policy_dependency" or
            "CONTRACT_DENY" or
            "TABLE_INACTIVE" or
            "POLICY_UNAVAILABLE" or
            "IDENTITY_INCOMPLETE" or
            "TENANT_UNRESOLVED" or
            "CONSENT_DENY" or
            "WRITE_TRANSPORT_NOT_WIRED");
    }

    private static (DiscoveryAccessState Access, DiscoveryRowScope? RowScope) MapAccessAndRowScope(
        TableAccessDecision decision,
        bool isPendingRequest,
        bool isRecentlyExpired)
    {
        var access = AccessStateOf(decision.IsAllowed, isPendingRequest, isRecentlyExpired);
        return access == DiscoveryAccessState.Granted
            ? (access, HasRowPredicate(decision) ? DiscoveryRowScope.RowRestricted : DiscoveryRowScope.Full)
            : (access, null);
    }

    /// <summary>
    /// The one precedence rule of the caller's access state (issues #798 / #804: the per-object state and the <c>accessStatus</c> filter
    /// both use it): an allowed read wins, then a pending request, then a recently expired grant, otherwise none.
    /// </summary>
    internal static DiscoveryAccessState AccessStateOf(bool isAllowed, bool isPendingRequest, bool isRecentlyExpired) =>
        isAllowed ? DiscoveryAccessState.Granted
        : isPendingRequest ? DiscoveryAccessState.Requested
        : isRecentlyExpired ? DiscoveryAccessState.Expired
        : DiscoveryAccessState.None;

    internal static bool HasRowPredicate(TableAccessDecision decision) =>
        decision.RowPolicy is not null ||
        decision.MandatoryRowPolicy is not null ||
        decision.ConsentFilterRecipe is not null ||
        !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
        !string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql);

    internal static (DiscoveryColumnState State, bool ClearInSomeRows) MapColumnState(
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

    public static (bool Pii, bool ClearRequiresSeparateApproval, IReadOnlyList<DiscoveryColumnClassification> Classifications, IReadOnlyList<string> ClearRequestable)
        ResolveColumnClassifications(TableMetadata table, TableColumn column, bool isHidden, string? tenantId = null,
            IReadOnlyDictionary<string, DataClassification>? classificationDefinitions = null, IReadOnlyList<ColumnClassification>? overrideRows = null)
    {
        if (isHidden || column.DiscoveryHidden) return (false, false, [], []);
        IReadOnlyList<ColumnClassification> effective = overrideRows is { Count: > 0 }
            ? ClassificationMetadata.DeduplicateScoped(overrideRows, tenantId)
            : (ClassificationMetadata.TryGetEffectiveColumnClassifications(table, column.ColumnName, tenantId, out var fromTable) ? fromTable : []);
        if (effective.Count == 0)
        {
            var sens = ClassificationMetadata.IsColumnSensitive(table, column);
            var fallbackPii = sens || CatalogPiiDetector.IsPii(column.ColumnName, sens);
            return (fallbackPii, fallbackPii, [], []);
        }

        var defs = classificationDefinitions ?? ClassificationAccessResolver.BuiltInCatalog(tenantId ?? ClassificationScopes.Global).Definitions;
        var items = new List<DiscoveryColumnClassification>(effective.Count);
        var clearReq = new List<string>();
        bool pii = false, sep = false;
        foreach (var cc in effective)
        {
            defs.TryGetValue(cc.Code, out var def);
            items.Add(new DiscoveryColumnClassification(cc.Code, def?.DisplayName ?? cc.Code));
            if (ClassificationMetadata.IsPiiCode(cc.Code, defs)) pii = true;
            if (def is { IsActive: true, SeparateApproval: true }) { sep = true; if (def.ClearGrantable) clearReq.Add(cc.Code); }
        }
        return (pii, sep, items, clearReq);
    }

    public static bool IsSampleVisibleState(DiscoveryColumnState state) => state != DiscoveryColumnState.NoAccess;
}
