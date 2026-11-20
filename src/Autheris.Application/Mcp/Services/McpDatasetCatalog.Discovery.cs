namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using static Autheris.Application.Mcp.Services.McpDatasetTools;

public sealed partial class McpDatasetCatalog
{
    private async Task<McpDatasetList> ListDatasetsWithDiscoveryAsync(
        ClaimsPrincipal principal,
        string? search,
        string? domain,
        int? offset,
        int? limit,
        string? tag,
        string? language,
        bool? overview,
        string? format,
        DiscoveryAccessState? access,
        CancellationToken ct)
    {
        var negotiatedLanguage = _languageNegotiator?.NegotiateLanguage(
            language,
            _localizationOptions?.Value.SupportedLanguages,
            _localizationOptions?.Value.DefaultLanguage);
        var caller = new CatalogCaller(
            principal,
            McpCatalogVisibility.SafeTenantId(principal),
            CatalogChannel.Mcp);

        if (overview == true)
        {
            List<McpDomainCount> domainCounts;
            int overviewTotal;
            if (string.IsNullOrWhiteSpace(domain) && string.IsNullOrWhiteSpace(search) && string.IsNullOrWhiteSpace(tag))
            {
                var facets = await _discoveryService!.FacetsAsync(access, caller, ct).ConfigureAwait(false);
                overviewTotal = facets.TotalCount;
                domainCounts = facets.Domains
                    .Select(d => new McpDomainCount(d.Domain, d.Count))
                    .OrderByDescending(d => d.TableCount)
                    .ToList();
            }
            else
            {
                var allPage = await _discoveryService!.ListAsync(
                    new DiscoveryQuery(domain, Schema: null, search, tag, negotiatedLanguage, First: DiscoveryService.MaxPageSize, After: 0, DiscoverySurface.Mcp, access),
                    caller,
                    ct).ConfigureAwait(false);
                overviewTotal = allPage.TotalCount;
                domainCounts = allPage.Items
                    .GroupBy(t => t.Domain, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new McpDomainCount(g.Key, g.Count()))
                    .OrderByDescending(d => d.TableCount)
                    .ToList();
            }

            return new McpDatasetList(
                Datasets: [],
                Total: overviewTotal,
                Truncated: false,
                Guidance: _hints.OverviewGuidance(overviewTotal, domainCounts.Count),
                Endpoints: null,
                Offset: 0,
                Limit: 0,
                NextOffset: null,
                Language: negotiatedLanguage,
                RequestedLanguage: language,
                Domains: domainCounts);
        }

        var effectiveOffset = Math.Max(0, offset ?? 0);
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, MaxListedDatasets);

        if (_procedureCatalog == null)
        {
            var page = await _discoveryService!.ListAsync(
                new DiscoveryQuery(domain, Schema: null, search, tag, negotiatedLanguage, First: effectiveLimit, After: effectiveOffset, DiscoverySurface.Mcp, access),
                caller,
                ct).ConfigureAwait(false);
            var pagedOnly = new List<McpDatasetSummary>(page.Items.Count);
            foreach (var obj in page.Items)
            {
                pagedOnly.Add(await BuildDiscoverySummaryAsync(obj, ct).ConfigureAwait(false));
            }

            return BuildDiscoveryListResult(pagedOnly, page.TotalCount, effectiveOffset, effectiveLimit, format, negotiatedLanguage, language);
        }

        // Procedures are listed as GRANTED; any other access filter leaves them out.
        var matchingProcs = new List<(string Id, RegisteredProcedure Proc)>();
        if (access is null or DiscoveryAccessState.Granted && string.IsNullOrWhiteSpace(tag))
        {
            var visibleProcs = await _procedureCatalog.ListVisibleAsync(principal, principal.GetTenantId(), ct).ConfigureAwait(false);
            foreach (var proc in visibleProcs.Where(p => p.Definition.Mode == ProcedureMode.Read && !string.IsNullOrWhiteSpace(p.Definition.ResultTable)))
            {
                var def = proc.Definition;
                var procId = $"procedure:{def.Name}";
                if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(def.CatalogDomain, domain.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var s = search.Trim();
                    bool matches = procId.Contains(s, StringComparison.OrdinalIgnoreCase)
                        || def.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                        || def.Summary.Contains(s, StringComparison.OrdinalIgnoreCase)
                        || (def.Description?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                        || def.Parameters.Any(p => p.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
                    if (!matches)
                    {
                        continue;
                    }
                }

                matchingProcs.Add((procId, proc));
            }
        }

        if (matchingProcs.Count == 0)
        {
            var page = await _discoveryService!.ListAsync(
                new DiscoveryQuery(domain, Schema: null, search, tag, negotiatedLanguage, First: effectiveLimit, After: effectiveOffset, DiscoverySurface.Mcp, access, IncludeProcedures: false),
                caller,
                ct).ConfigureAwait(false);
            var pagedOnly = new List<McpDatasetSummary>(page.Items.Count);
            foreach (var obj in page.Items)
            {
                pagedOnly.Add(await BuildDiscoverySummaryAsync(obj, ct).ConfigureAwait(false));
            }

            return BuildDiscoveryListResult(pagedOnly, page.TotalCount, effectiveOffset, effectiveLimit, format, negotiatedLanguage, language);
        }

        var candidates = new List<(string Id, DiscoveryObject? Obj, RegisteredProcedure? Proc)>(matchingProcs.Count + effectiveLimit);
        var neededTables = effectiveOffset + effectiveLimit;
        var tableTotalCount = 0;
        var fetchedCount = 0;
        while (fetchedCount < neededTables)
        {
            var batchSize = Math.Min(DiscoveryService.MaxPageSize, neededTables - fetchedCount);
            var tablePage = await _discoveryService!.ListAsync(
                new DiscoveryQuery(domain, Schema: null, search, tag, negotiatedLanguage, First: batchSize, After: fetchedCount, DiscoverySurface.Mcp, access, IncludeProcedures: false),
                caller,
                ct).ConfigureAwait(false);
            tableTotalCount = tablePage.TotalCount;
            foreach (var obj in tablePage.Items)
            {
                if (obj.Kind == DiscoveryObjectKind.Procedure)
                {
                    continue;
                }

                candidates.Add(($"{obj.Domain}.{obj.Schema}.{obj.Name}", obj, null));
            }

            fetchedCount += tablePage.Items.Count;
            if (!tablePage.HasNextPage || tablePage.Items.Count == 0)
            {
                break;
            }
        }

        foreach (var (procId, proc) in matchingProcs)
        {
            candidates.Add((procId, null, proc));
        }

        var sorted = candidates.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var paged = sorted.Skip(effectiveOffset).Take(effectiveLimit).ToList();
        var summaries = new List<McpDatasetSummary>(paged.Count);
        foreach (var item in paged)
        {
            if (item.Obj is { } obj)
            {
                summaries.Add(await BuildDiscoverySummaryAsync(obj, ct).ConfigureAwait(false));
            }
            else if (item.Proc is { } proc)
            {
                summaries.Add(await BuildProcedureSummaryAsync(principal, item.Id, proc, ct).ConfigureAwait(false));
            }
        }

        return BuildDiscoveryListResult(summaries, tableTotalCount + matchingProcs.Count, effectiveOffset, effectiveLimit, format, negotiatedLanguage, language);
    }

    private async Task<McpDatasetSummary> BuildDiscoverySummaryAsync(DiscoveryObject obj, CancellationToken ct)
    {
        var id = new TableIdentifier(obj.Domain, obj.Schema, obj.Name);
        var visibleColumnCount = DiscoveryColumnVisibility.VisibleCount(obj);
        var summaryOnly = obj.Columns.Count == 0;
        var graphQl = graphQlMap == null || summaryOnly ? null : await graphQlMap.GetTableAsync(id, ct).ConfigureAwait(false);
        var displayName = DiscoveryTextSanitizer.SanitizeText(obj.DisplayName, DiscoveryTextSanitizer.MaxDisplayNameRunes) ?? McpCatalogText.DisplayName(null, obj.Name);
        var description = DiscoveryTextSanitizer.SanitizeText(obj.Description, DiscoveryTextSanitizer.MaxDescriptionRunes);
        var status = MapDiscoveryAccessStatus(obj.Access);
        return new McpDatasetSummary(
            $"{obj.Domain}.{obj.Schema}.{obj.Name}",
            displayName,
            description,
            obj.Sensitivity,
            visibleColumnCount,
            graphQl?.QueryField,
            Tags: obj.Tags,
            AccessStatus: status,
            Kind: obj.Kind.ToWireName(),
            Size: null,
            DocumentCount: obj.DocumentCount > 0 ? obj.DocumentCount : null);
    }

    private async Task<McpDatasetSummary> BuildProcedureSummaryAsync(ClaimsPrincipal principal, string procId, RegisteredProcedure proc, CancellationToken ct)
    {
        var def = proc.Definition;
        int columnCount = 0;
        string sensitivity = "NORMAL";
        if (def.ResultTableIdentifier is { } resId)
        {
            var resMeta = await _metadataRepository.GetTableMetadataAsync(resId, ct).ConfigureAwait(false);
            if (resMeta != null)
            {
                sensitivity = resMeta.Table.Sensitivity ?? "NORMAL";
                var (activeConsents, _, isAdmin) = await GetCallerConsentsAndRequestsAsync(principal, ct).ConfigureAwait(false);
                var levels = ResolveColumnLevels(principal, resMeta, activeConsents, isAdmin, isGranted: true, bypass: IsBypassed);
                columnCount = resMeta.Columns.Count(col => levels.TryGetValue(col.ColumnName, out var lvl) && lvl != Autheris.Domain.Interfaces.ColumnAccessLevel.Deny);
            }
        }

        return new McpDatasetSummary(
            Id: procId,
            DisplayName: McpCatalogText.DisplayName(def.Summary, def.Name),
            Description: DiscoveryTextSanitizer.SanitizeText(def.Description ?? def.Summary, DiscoveryTextSanitizer.MaxDescriptionRunes),
            Sensitivity: sensitivity,
            ColumnCount: columnCount,
            GraphQlField: null,
            Tags: null,
            AccessStatus: "GRANTED",
            Kind: "procedure");
    }

    private McpDatasetList BuildDiscoveryListResult(
        IReadOnlyList<McpDatasetSummary> summaries,
        int total,
        int effectiveOffset,
        int effectiveLimit,
        string? format,
        string? negotiatedLanguage,
        string? requestedLanguage)
    {
        var hasMore = effectiveOffset + summaries.Count < total;
        int? nextOffset = hasMore ? effectiveOffset + summaries.Count : null;
        var guidance = _hints.Guidance();
        if (total > 100)
        {
            var tip = _hints.Tip(total);
            guidance = guidance is null ? (tip.TrimStart() is { Length: > 0 } t ? t : null) : guidance + tip;
        }

        var isCompact = string.Equals(format, "compact", StringComparison.OrdinalIgnoreCase);
        var endpoints = isCompact || effectiveOffset > 0 ? null : Endpoints();
        return new McpDatasetList(
            summaries,
            total,
            hasMore,
            guidance,
            endpoints,
            effectiveOffset,
            effectiveLimit,
            nextOffset,
            Language: negotiatedLanguage,
            RequestedLanguage: requestedLanguage);
    }

    private async Task<McpDatasetDescription> DescribeDatasetWithDiscoveryAsync(
        ClaimsPrincipal principal,
        string dataset,
        string? language,
        bool brief,
        CancellationToken ct)
    {
        if (!TryParseDatasetId(dataset, out var id))
        {
            throw new ArgumentException("The dataset id must have the form 'domain.schema.table'.", nameof(dataset));
        }

        var negotiatedLanguage = _languageNegotiator?.NegotiateLanguage(
            language,
            _localizationOptions?.Value.SupportedLanguages,
            _localizationOptions?.Value.DefaultLanguage);
        var caller = new CatalogCaller(
            principal,
            McpCatalogVisibility.SafeTenantId(principal),
            CatalogChannel.Mcp);

        var obj = await _discoveryService!.GetAsync(id, caller, ct).ConfigureAwait(false)
            ?? throw new TableNotFoundException(id);

        var datasetId = $"{obj.Domain}.{obj.Schema}.{obj.Name}";
        var displayName = DiscoveryTextSanitizer.SanitizeText(obj.DisplayName, DiscoveryTextSanitizer.MaxDisplayNameRunes) ?? McpCatalogText.DisplayName(null, obj.Name);
        var description = DiscoveryTextSanitizer.SanitizeText(obj.Description, DiscoveryTextSanitizer.MaxDescriptionRunes);
        var accessStatus = MapDiscoveryAccessStatus(obj.Access);

        var linked = McpLinkedDocuments.From(obj);
        var ownerLabels = obj.Owners
            .Select(static o => !string.IsNullOrWhiteSpace(o.Email) ? $"{o.DisplayName} <{o.Email}>" : o.DisplayName)
            .ToList();
        var dataOwner = ownerLabels.FirstOrDefault();
        var delegates = ownerLabels.Count > 1 ? ownerLabels.Skip(1).ToList() : null;

        if (obj.Columns.Count == 0)
        {
            return new McpDatasetDescription(
                datasetId,
                displayName,
                obj.Domain,
                obj.Schema,
                obj.Name,
                description,
                LongDescription: null,
                obj.Sensitivity,
                RequiresApproval: false,
                Columns: [],
                Examples: [],
                GraphQl: null,
                OtherAccess: null,
                Tags: obj.Tags,
                DataOwner: dataOwner,
                DataOwnerDelegates: delegates,
                AccessStatus: accessStatus,
                Indexes: null,
                View: SummaryView,
                Kind: obj.Kind.ToWireName(),
                Language: negotiatedLanguage,
                RequestedLanguage: language)
            {
                Documents = linked.Documents,
                Applications = linked.Applications,
                DocumentsTruncated = linked.Truncated
            };
        }

        var graphQl = brief || graphQlMap == null ? null : await graphQlMap.GetTableAsync(id, ct).ConfigureAwait(false);
        var graphQlColumns = (graphQl?.Columns ?? []).ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        // #805/#807: keys, indexes and required filters as projected for this caller by the discovery core.
        var shape = obj.QueryShape ?? DiscoveryQueryShape.Empty;
        var primaryKeys = new HashSet<string>(shape.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        var columns = DiscoveryColumnVisibility.VisibleColumns(obj)
            .Select(c =>
            {
                var access = c.State == DiscoveryColumnState.Clear ? "clear" : "mask";
                if (brief)
                {
                    // Issue #799: a column signature only (name, type, key flag, access).
                    return new McpDatasetColumn(c.Name, c.DataType, Description: null, c.Pii, primaryKeys.Contains(c.Name), Access: access, Role: c.Role?.ToWireName());
                }

                graphQlColumns.TryGetValue(c.Name, out var field);
                var colProfile = !obj.HiddenTextColumns.Contains(c.Name) && c.Statistics != null
                    ? new ColumnValueProfile(
                        MinValue: c.Statistics.MinValue,
                        MaxValue: c.Statistics.MaxValue,
                        EmptyShare: c.Statistics.NullRatio ?? 0.0,
                        TypicalLength: c.Statistics.MaxLength,
                        ApproximateDistinctCount: c.Statistics.ApproxDistinctCount,
                        AllowedValues: c.Statistics.TopValues)
                    : null;

                return new McpDatasetColumn(
                    c.Name,
                    c.DataType,
                    obj.HiddenTextColumns.Contains(c.Name) ? null : DiscoveryTextSanitizer.SanitizeText(c.Description, DiscoveryTextSanitizer.MaxDescriptionRunes),
                    c.Pii,
                    PrimaryKey: primaryKeys.Contains(c.Name),
                    GraphQlField: field?.Field,
                    GraphQlType: field?.GraphQlType,
                    ValueProfile: colProfile,
                    Access: access,
                    DisplayName: obj.HiddenTextColumns.Contains(c.Name) ? null : DiscoveryTextSanitizer.SanitizeText(c.DisplayName, DiscoveryTextSanitizer.MaxDisplayNameRunes),
                    Classifications: c.Classifications, ClearRequestable: c.ClearRequestable,
                    LongDescription: obj.HiddenTextColumns.Contains(c.Name) ? null : c.LongDescription,
                    Role: c.Role?.ToWireName())
                {
                    Documents = linked.ColumnDocuments.TryGetValue(c.Name, out var columnDocs) ? columnDocs : null
                };
            })
            .ToList();

        var hiddenCols = obj.Columns
            .Where(c => obj.HiddenTextColumns.Contains(c.Name))
            .Select(static c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<McpDatasetExample> examples = brief
            ? []
            : await LoadExamplesAsync(principal, id, graphQl, graphQlColumns, ExampleHiddenColumns(hiddenCols, graphQl, obj), ct).ConfigureAwait(false);

        var graphQlAccess = brief || graphQl == null
            ? null
            : await GraphQlAccessAsync(principal, graphQl, columns, ct, hiddenCols).ConfigureAwait(false);

        return new McpDatasetDescription(
            datasetId,
            displayName,
            obj.Domain,
            obj.Schema,
            obj.Name,
            description,
            LongDescription: null,
            obj.Sensitivity,
            RequiresApproval: false,
            columns,
            examples,
            graphQlAccess,
            brief ? null : DatasetAccess(id),
            Tags: obj.Tags,
            DataOwner: dataOwner,
            DataOwnerDelegates: delegates,
            AccessStatus: accessStatus,
            Indexes: shape.Indexes,
            View: brief ? BriefView : null,
            Kind: obj.Kind.ToWireName(),
            RequiredFilterColumns: shape.RequiredFilterColumns.Count == 0 ? null : shape.RequiredFilterColumns,
            RequiredFilterColumnGroups: shape.RequiredFilterColumnGroups.Count == 0 ? null : shape.RequiredFilterColumnGroups,
            Language: negotiatedLanguage,
            RequestedLanguage: language,
            PrimaryKeyOrder: Autheris.Application.DataSources.Services.DatasetKeyProjection.KeyOrder(shape.PrimaryKeyColumns, shape.PrimaryKeyDirections))
        {
            Documents = linked.Documents,
            Applications = linked.Applications,
            DocumentsTruncated = linked.Truncated
        };
    }

    private static string MapDiscoveryAccessStatus(DiscoveryAccessState state) => CatalogAccessStatus.Of(state);

    private static bool Matches(TableMetadata table, string search)
    {
        var id = table.Identifier;
        if (id.TableName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            id.Domain.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            id.Schema.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (search.Contains('.', StringComparison.Ordinal) && DatasetId(id).Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (table.Table.Description != null && table.Table.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (table.Table.Texts.Entries.Count > 0 && table.Table.Texts.Entries.Values.Any(e => e.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true))
        {
            return true;
        }

        if (table.Columns.Count > 0)
        {
            foreach (var c in table.Columns)
            {
                if (c.ColumnName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (c.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (c.Texts.Entries.Count > 0 && c.Texts.Entries.Values.Any(e => e.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
