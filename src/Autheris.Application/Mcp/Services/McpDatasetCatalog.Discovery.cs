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
        TableIdentifier? graphQlTarget,
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

        if (graphQlTarget is { } target)
        {
            return await ListSingleGraphQlDatasetWithDiscoveryAsync(
                target, domain, search, tag, negotiatedLanguage, language, offset, limit, overview, format, access, caller, ct).ConfigureAwait(false);
        }

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
            DocumentCount: obj.DocumentCount > 0 ? obj.DocumentCount : null,
            SqlName: obj.Kind == DiscoveryObjectKind.Procedure ? null : SqlNameOf(id, options?.Value));
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
                RequestedLanguage: language,
                SqlName: obj.Kind == DiscoveryObjectKind.Procedure ? null : SqlNameOf(id, options?.Value))
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
                    Role: c.Role?.ToWireName(),
                    SqlName: c.Name)
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
            PrimaryKeyOrder: Autheris.Application.DataSources.Services.DatasetKeyProjection.KeyOrder(shape.PrimaryKeyColumns, shape.PrimaryKeyDirections),
            SqlName: obj.Kind == DiscoveryObjectKind.Procedure ? null : SqlNameOf(id, options?.Value))
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

    private async Task<McpDatasetList> ListSingleGraphQlDatasetWithDiscoveryAsync(
        TableIdentifier target,
        string? domain,
        string? search,
        string? tag,
        string? negotiatedLanguage,
        string? requestedLanguage,
        int? offset,
        int? limit,
        bool? overview,
        string? format,
        DiscoveryAccessState? access,
        CatalogCaller caller,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(target.Domain, domain.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return EmptyDatasetList(offset, limit, negotiatedLanguage ?? requestedLanguage, overview);
        }

        DiscoveryObject? match;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var page = await _discoveryService!.ListAsync(
                new DiscoveryQuery(target.Domain, target.Schema, search, tag, negotiatedLanguage, First: 100, After: 0, DiscoverySurface.Mcp, access, IncludeProcedures: false),
                caller,
                ct).ConfigureAwait(false);
            match = page.Items.FirstOrDefault(o =>
                o.Kind != DiscoveryObjectKind.Procedure &&
                string.Equals(o.Domain, target.Domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.Schema, target.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.Name, target.TableName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            match = await _discoveryService!.GetAsync(target, caller, ct).ConfigureAwait(false);
            if (match != null)
            {
                if (match.Kind == DiscoveryObjectKind.Procedure ||
                    (access is { } wanted && match.Access != wanted) ||
                    (!string.IsNullOrWhiteSpace(tag) && !match.Tags.Any(t => string.Equals(t, tag.Trim(), StringComparison.OrdinalIgnoreCase))))
                {
                    match = null;
                }
            }
        }

        if (match is null)
        {
            return EmptyDatasetList(offset, limit, negotiatedLanguage ?? requestedLanguage, overview);
        }

        if (overview == true)
        {
            return new McpDatasetList(
                Datasets: [],
                Total: 1,
                Truncated: false,
                Guidance: _hints.OverviewGuidance(1, 1),
                Endpoints: null,
                Offset: 0,
                Limit: 0,
                NextOffset: null,
                Language: negotiatedLanguage,
                RequestedLanguage: requestedLanguage,
                Domains: [new McpDomainCount(match.Domain, 1)]);
        }

        var effectiveOffset = Math.Max(0, offset ?? 0);
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, MaxListedDatasets);
        var items = effectiveOffset == 0
            ? new List<McpDatasetSummary> { await BuildDiscoverySummaryAsync(match, ct).ConfigureAwait(false) }
            : [];
        return BuildDiscoveryListResult(items, total: 1, effectiveOffset, effectiveLimit, format, negotiatedLanguage, requestedLanguage);
    }

    private McpDatasetList EmptyDatasetList(int? offset, int? limit, string? language, bool? overview)
    {
        var negotiatedLanguage = _languageNegotiator?.NegotiateLanguage(
            language,
            _localizationOptions?.Value.SupportedLanguages,
            _localizationOptions?.Value.DefaultLanguage);

        if (overview == true)
        {
            return new McpDatasetList(
                Datasets: [],
                Total: 0,
                Truncated: false,
                Guidance: _hints.OverviewGuidance(0, 0),
                Endpoints: null,
                Offset: 0,
                Limit: 0,
                NextOffset: null,
                Language: negotiatedLanguage,
                RequestedLanguage: language,
                Domains: []);
        }

        var effectiveOffset = Math.Max(0, offset ?? 0);
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, MaxListedDatasets);
        return BuildDiscoveryListResult([], total: 0, effectiveOffset, effectiveLimit, format: null, negotiatedLanguage, language);
    }

    private async Task<string> ResolveDescribeTargetAsync(string? dataset, string? graphQlField, string? sqlName, CancellationToken ct)
    {
        bool hasDataset = !string.IsNullOrWhiteSpace(dataset);
        bool hasGraphQlField = !string.IsNullOrWhiteSpace(graphQlField);
        bool hasSqlName = !string.IsNullOrWhiteSpace(sqlName);
        int provided = (hasDataset ? 1 : 0) + (hasGraphQlField ? 1 : 0) + (hasSqlName ? 1 : 0);
        if (provided > 1)
        {
            throw new ArgumentException("Specify only one of 'dataset', 'graphQlField', or 'sqlName'.");
        }

        if (provided == 0)
        {
            throw new ArgumentException("One of 'dataset', 'graphQlField', or 'sqlName' is required.");
        }

        if (hasDataset)
        {
            return dataset!;
        }

        if (hasSqlName)
        {
            return sqlName!;
        }

        var resolved = graphQlMap is null ? null : await graphQlMap.ResolveRootFieldAsync(graphQlField!, ct).ConfigureAwait(false);
        return resolved is { } id
            ? DatasetId(id)
            : throw new TableNotFoundException(new TableIdentifier("_", "_", "_"));
    }

    private async Task<EffectiveTableAccessDto> DescribeMyAccessCoreAsync(
        ClaimsPrincipal principal,
        string? dataset,
        string? graphQlField,
        string? sqlName,
        CancellationToken ct)
    {
        bool hasDataset = !string.IsNullOrWhiteSpace(dataset);
        bool hasGraphQlField = !string.IsNullOrWhiteSpace(graphQlField);
        bool hasSqlName = !string.IsNullOrWhiteSpace(sqlName);
        int provided = (hasDataset ? 1 : 0) + (hasGraphQlField ? 1 : 0) + (hasSqlName ? 1 : 0);
        if (provided > 1)
        {
            throw new ArgumentException("Specify only one of 'dataset', 'graphQlField', or 'sqlName'.");
        }

        if (provided == 0)
        {
            throw new ArgumentException("Invalid dataset format: ''. Expected 'domain.schema.table' or 'domain.table'.", nameof(dataset));
        }

        if (hasDataset || hasSqlName)
        {
            var raw = hasDataset ? dataset! : sqlName!;
            if (!TryParseDatasetId(raw, out var tableId))
            {
                throw new ArgumentException($"Invalid dataset format: '{raw}'. Expected 'domain.schema.table' or 'domain.table'.", hasDataset ? nameof(dataset) : nameof(sqlName));
            }

            return await explanationService!.ExplainMyAccessAsync(principal, tableId, ct).ConfigureAwait(false);
        }

        var resolved = graphQlMap is null ? null : await graphQlMap.ResolveRootFieldAsync(graphQlField!, ct).ConfigureAwait(false);
        if (resolved is not { } targetId)
        {
            return NotFoundAccessDto(graphQlField!);
        }

        var dto = await explanationService!.ExplainMyAccessAsync(principal, targetId, ct).ConfigureAwait(false);
        if (string.Equals(dto.Decision, "DENIED", StringComparison.OrdinalIgnoreCase) &&
            dto.AllowedColumns.Count == 0 &&
            dto.MaskedColumns.Count == 0 &&
            dto.DeniedColumns.Count == 0 &&
            dto.ActiveRowFilters.Count == 0)
        {
            return dto with { Table = graphQlField! };
        }

        return dto;
    }

    private static EffectiveTableAccessDto NotFoundAccessDto(string tableLabel) => new(
        tableLabel,
        "DENIED",
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Autheris.Application.Governance.Services.EffectiveAccessExplanationService.TableNotFoundMessage,
        null);

    private static bool SameTable(TableIdentifier a, TableIdentifier b) =>
        string.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.TableName, b.TableName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Three-part SQL name (<c>catalog.schema.table</c>) as WebSQL accepts it, preserving the exact spelling/casing
    /// from <see cref="Autheris.Application.Sql.Services.SqlDataSourceResolver"/> for the catalog part and from the table metadata for schema and table.
    /// </summary>
    public static string SqlNameOf(TableIdentifier id, Autheris.Domain.Options.GatewayOptions? gatewayOptions = null)
    {
        var catalog = gatewayOptions?.WebSql is { } webSql &&
            Autheris.Application.Sql.Services.SqlDataSourceResolver.TryResolveGloballyAllowedDataSource(webSql, id.Domain, out var resolved)
            ? resolved
            : id.Domain;
        return $"{catalog}.{id.Schema}.{id.TableName}";
    }

    /// <summary>The non-GraphQL ways to read one dataset.</summary>
    private IReadOnlyList<McpAccessPath> DatasetAccess(TableIdentifier id)
    {
        var o = options?.Value ?? new Autheris.Domain.Options.GatewayOptions();
        var paths = new List<McpAccessPath>();

        if (o.OData.Enabled)
        {
            paths.Add(new("OData v4", "GET", $"/odata/v4/{id.Domain}/{id.Schema}/{id.TableName}",
                "OData query options $select, $filter, $orderby, $top, $skip. JSON with OData annotations."));
            paths.Add(new("OpenAPI (REST)", "GET", $"/odata/v4/{id.Domain}/openapi.json",
                "OpenAPI 3.1 description of the REST routes of this domain."));
        }

        if (_hints.Tool(SampleRows))
        {
            paths.Add(new("MCP sample_rows", "MCP", SampleRows, "A few rows of this dataset, for a first look."));
        }

        if (o.Arrow.Enabled)
        {
            paths.Add(new("Arrow IPC export", "POST", "/api/v1/export/arrow",
                $"Body {{\"table\": \"{id.Domain}.{id.Schema}.{id.TableName}\"}}: columnar bulk export (application/vnd.apache.arrow.stream)."));
        }

        return paths;
    }

    /// <summary>All enabled protocols of the gateway, GraphQL first.</summary>
    private IReadOnlyList<McpAccessPath> Endpoints()
    {
        var o = options?.Value ?? new Autheris.Domain.Options.GatewayOptions();
        var endpoints = new List<McpAccessPath>
        {
            new("GraphQL", "POST", GraphQlEndpoint,
                _hints.GraphQlEndpointDescription(),
                Preferred: true)
        };

        if (o.OData.Enabled)
        {
            endpoints.Add(new("OData v4", "GET", "/odata/v4",
                "Service document; /odata/v4/$metadata is the CSDL schema, /odata/v4/{domain}/{schema}/{table} the entity sets."));
            endpoints.Add(new("OpenAPI (REST)", "GET", "/api/v1/openapi/index", "Index of the OpenAPI 3.1 documents per domain."));
        }

        if (o.WebSql.Enabled)
        {
            endpoints.Add(new("WebSQL", "POST", "/api/v1/sql",
                "Body {\"sql\", \"parameters\", \"dataSource\"}: governed SQL in Trino syntax with 3-part names (<catalog>.<schema>.<table>) or 2-part names (<schema>.<table>)."));
            endpoints.Add(new("Trino Protocol", "POST", "/v1/statement",
                "Standard Trino REST Client protocol: query in body, X-Trino-Wait-Timeout for sync/async execution, absolute nextUri continuation, DELETE nextUri cancels."));
        }

        if (o.SqlEndpoints.Enabled)
        {
            endpoints.Add(new("Saved SQL queries", "GET", "/api/v1/queries", "Lists the curated SQL endpoints; call one with GET or POST /api/v1/queries/{name}."));
        }

        if (o.SqlEndpoints.Procedures.Enabled)
        {
            endpoints.Add(new("Stored procedures", "GET", "/api/v1/procedures", "Lists the published procedures; call one with GET or POST /api/v1/procedures/{name}."));
        }

        if (o.DuckDbOlap.Enabled)
        {
            endpoints.Add(new("DuckDB OLAP", "POST", "/api/v1/olap/query",
                "Body {\"sql\", \"tableNames\", \"limit\"}: analytical SQL across several datasets."));
        }

        if (o.Arrow.Enabled)
        {
            endpoints.Add(new("Arrow IPC export", "POST", "/api/v1/export/arrow", "Columnar bulk export of one dataset."));
        }

        if (o.Lakehouse.Enabled)
        {
            endpoints.Add(new("Iceberg REST Catalog", "GET", "/modules/lakehouse/iceberg/v1/{prefix}/namespaces", "Apache Iceberg REST Catalog protocol endpoint."));
        }

        return endpoints;
    }
}
