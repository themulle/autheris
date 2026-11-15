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
            var allPage = await _discoveryService!.ListAsync(
                new DiscoveryQuery(domain, Schema: null, search, tag, negotiatedLanguage, First: 10_000, After: 0, DiscoverySurface.Mcp),
                caller,
                ct).ConfigureAwait(false);
            var domainCounts = allPage.Items
                .GroupBy(t => t.Domain, StringComparer.OrdinalIgnoreCase)
                .Select(g => new McpDomainCount(g.Key, g.Count()))
                .OrderByDescending(d => d.TableCount)
                .ToList();

            return new McpDatasetList(
                Datasets: [],
                Total: allPage.TotalCount,
                Truncated: false,
                Guidance: _hints.OverviewGuidance(allPage.TotalCount, domainCounts.Count),
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
        var candidates = new List<(string Id, DiscoveryObject? Obj, RegisteredProcedure? Proc)>();

        if (_procedureCatalog == null)
        {
            var page = await _discoveryService!.ListAsync(
                new DiscoveryQuery(domain, Schema: null, search, tag, negotiatedLanguage, First: effectiveLimit, After: effectiveOffset, DiscoverySurface.Mcp),
                caller,
                ct).ConfigureAwait(false);
            var pagedOnly = new List<McpDatasetSummary>(page.Items.Count);
            foreach (var obj in page.Items)
            {
                pagedOnly.Add(await BuildDiscoverySummaryAsync(obj, ct).ConfigureAwait(false));
            }

            return BuildDiscoveryListResult(pagedOnly, page.TotalCount, effectiveOffset, effectiveLimit, format, negotiatedLanguage, language);
        }

        var tablePage = await _discoveryService!.ListAsync(
            new DiscoveryQuery(domain, Schema: null, search, tag, negotiatedLanguage, First: 10_000, After: 0, DiscoverySurface.Mcp),
            caller,
            ct).ConfigureAwait(false);
        foreach (var obj in tablePage.Items)
        {
            candidates.Add(($"{obj.Domain}.{obj.Schema}.{obj.Name}", obj, null));
        }

        var visibleProcs = await _procedureCatalog.ListVisibleAsync(principal, principal.GetTenantId(), ct).ConfigureAwait(false);
        foreach (var proc in visibleProcs.Where(p => p.Definition.Mode == ProcedureMode.Read && !string.IsNullOrWhiteSpace(p.Definition.ResultTable)))
        {
            var def = proc.Definition;
            var procId = $"procedure:{def.Name}";
            if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(def.CatalogDomain, domain.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tag))
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

        return BuildDiscoveryListResult(summaries, sorted.Count, effectiveOffset, effectiveLimit, format, negotiatedLanguage, language);
    }

    private async Task<McpDatasetSummary> BuildDiscoverySummaryAsync(DiscoveryObject obj, CancellationToken ct)
    {
        var id = new TableIdentifier(obj.Domain, obj.Schema, obj.Name);
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
            summaryOnly ? 0 : obj.Columns.Count,
            graphQl?.QueryField,
            Tags: obj.Tags,
            AccessStatus: status,
            Kind: obj.Kind.ToString().ToLowerInvariant(),
            Size: null);
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
                columnCount = resMeta.Columns.Count(col => !levels.TryGetValue(col.ColumnName, out var lvl) || lvl != Autheris.Domain.Interfaces.ColumnAccessLevel.Deny);
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
                Kind: obj.Kind.ToString().ToLowerInvariant(),
                Language: negotiatedLanguage,
                RequestedLanguage: language);
        }

        var graphQl = brief || graphQlMap == null ? null : await graphQlMap.GetTableAsync(id, ct).ConfigureAwait(false);
        var graphQlColumns = (graphQl?.Columns ?? []).ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        var columns = obj.Columns
            .Where(static c => c.State != DiscoveryColumnState.NoAccess)
            .Select(c =>
            {
                graphQlColumns.TryGetValue(c.Name, out var field);
                var colProfile = !brief && c.Statistics != null
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
                    DiscoveryTextSanitizer.SanitizeText(c.Description, DiscoveryTextSanitizer.MaxDescriptionRunes),
                    c.Pii,
                    PrimaryKey: false,
                    GraphQlField: field?.Field,
                    GraphQlType: field?.GraphQlType,
                    ValueProfile: colProfile,
                    Access: c.State == DiscoveryColumnState.Clear ? "clear" : "mask",
                    DisplayName: DiscoveryTextSanitizer.SanitizeText(c.DisplayName, DiscoveryTextSanitizer.MaxDisplayNameRunes));
            })
            .ToList();

        IReadOnlyList<McpDatasetExample> examples = [];
        if (!brief && goldenQueryService != null)
        {
            var goldens = await goldenQueryService.GetGoldenQueriesAsync(obj.Domain, obj.Name, ct).ConfigureAwait(false);
            examples = goldens.Select(g => new McpDatasetExample(g.Title, g.Description, g.QueryText, g.VariablesJson)).ToList();
        }

        var graphQlAccess = brief || graphQl == null
            ? null
            : await GraphQlAccessAsync(principal, graphQl, columns, ct).ConfigureAwait(false);

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
            Indexes: null,
            Kind: obj.Kind.ToString().ToLowerInvariant(),
            Language: negotiatedLanguage,
            RequestedLanguage: language);
    }

    private static string MapDiscoveryAccessStatus(DiscoveryAccessState state) => state switch
    {
        DiscoveryAccessState.Granted => "GRANTED",
        DiscoveryAccessState.Requested => "PENDING",
        DiscoveryAccessState.Expired => "EXPIRED",
        _ => "REQUEST_ACCESS"
    };

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

        // A Summary row matches on identifier and description only; Full rows carry no DENY-masked column (removed by the engine).
        if (table.CatalogView != CatalogView.Summary && table.Columns.Count > 0)
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
