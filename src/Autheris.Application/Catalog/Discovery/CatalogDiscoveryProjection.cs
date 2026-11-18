namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Projects <see cref="DiscoveryObject"/> records into existing REST/Application catalog DTOs.
/// </summary>
public static class CatalogDiscoveryProjection
{
    public static CatalogDatasetSummary ToDatasetSummary(DiscoveryObject obj, string? language) =>
        new(
            DatasetId: $"{obj.Domain}.{obj.Schema}.{obj.Name}",
            Domain: obj.Domain,
            Schema: obj.Schema,
            Table: obj.Name,
            SourceType: obj.Kind.ToString().ToLowerInvariant(),
            Sensitivity: obj.Sensitivity,
            Description: obj.Description,
            IsActive: true,
            DisplayName: obj.DisplayName,
            Language: language);

    public static CatalogDatasetDetail ToDatasetDetail(DiscoveryObject obj, string? language)
    {
        // #805/#807: keys, indexes and required filters as projected for this caller by the discovery core.
        var shape = obj.QueryShape ?? DiscoveryQueryShape.Empty;
        var primaryKeys = new HashSet<string>(shape.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        // Without access every column is listed as Deny, so the caller can see what to request; with access a column the caller is
        // denied is not listed at all.
        var granted = obj.Access == DiscoveryAccessState.Granted;
        var columns = obj.Columns.Where(c => !granted || c.State != DiscoveryColumnState.NoAccess).Select(c => new CatalogColumnDetail(
            Name: c.Name,
            Type: c.DataType,
            Sensitivity: c.Pii ? "Sensitive" : "Public",
            MaskingState: c.State switch
            {
                DiscoveryColumnState.Clear => "Clear",
                DiscoveryColumnState.Masked => "Masked",
                _ => "Deny"
            },
            IsPrimaryKey: primaryKeys.Contains(c.Name), IsPiiIndicator: c.Pii,
            DisplayName: obj.HiddenTextColumns.Contains(c.Name) ? null : (c.DisplayName ?? c.Name),
            Description: obj.HiddenTextColumns.Contains(c.Name) ? null : c.Description,
            Classifications: c.Classifications, ClearRequestable: c.ClearRequestable)).ToList();

        return new CatalogDatasetDetail(
            DatasetId: $"{obj.Domain}.{obj.Schema}.{obj.Name}",
            Domain: obj.Domain,
            Schema: obj.Schema,
            Table: obj.Name,
            Columns: columns,
            PrimaryKeys: shape.PrimaryKeyColumns,
            Sensitivity: obj.Sensitivity,
            IsActive: true,
            Indexes: shape.Indexes,
            DisplayName: obj.DisplayName,
            Description: obj.Description,
            LongDescription: null,
            Language: language,
            // Issue #390 contract: parallel to PrimaryKeys, null unless a column is descending.
            PrimaryKeyDirections: KeyDirections.AnyDescending(shape.PrimaryKeyDirections) ? KeyDirections.ToWire(shape.PrimaryKeyDirections) : null,
            RequiredFilterColumns: shape.RequiredFilterColumns.Count == 0 ? null : shape.RequiredFilterColumns,
            RequiredFilterColumnGroups: shape.RequiredFilterColumnGroups.Count == 0 ? null : shape.RequiredFilterColumnGroups);
    }

    public static CatalogSearchHit ToSearchHit(DiscoveryObject obj, string? effectiveMode, string? language) =>
        new(
            TableIdentifier: new TableIdentifier(obj.Domain, obj.Schema, obj.Name),
            DisplayName: obj.DisplayName ?? $"{obj.Domain}.{obj.Name}",
            Description: obj.Description,
            Domain: obj.Domain,
            Sensitivity: obj.Sensitivity,
            CombinedScore: 1.0,
            Bm25Score: 1.0,
            VectorScore: null,
            MatchedTerms: [],
            RelevantColumns: DiscoveryColumnVisibility.VisibleColumns(obj).Select(static c => c.Name).Take(5).ToList(),
            RelatedJoinPaths: [],
            SuggestedGraphQlField: null,
            EffectiveMode: effectiveMode,
            Language: language,
            Kind: obj.Kind.ToString().ToLowerInvariant(),
            DatasetId: $"{obj.Domain}.{obj.Schema}.{obj.Name}",
            AccessStatus: CatalogAccessStatus.Of(obj.Access));

    public static IReadOnlyList<CatalogDatasetSummary> FilterProcedureSummaries(
        IReadOnlyList<RegisteredProcedure> visibleProcedures,
        string? domain,
        string? trimmedQuery) =>
        visibleProcedures
            .Where(p => string.IsNullOrWhiteSpace(domain) || string.Equals(p.Definition.CatalogDomain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(p => string.IsNullOrWhiteSpace(trimmedQuery) ||
                        p.Definition.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                        p.Definition.Summary.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                        (p.Definition.Description?.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        p.Definition.Parameters.Any(param => param.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new CatalogDatasetSummary(
                DatasetId: $"procedure:{p.Definition.Name}",
                Domain: p.Definition.CatalogDomain,
                Schema: "procedure",
                Table: p.Definition.Name,
                SourceType: "procedure",
                Sensitivity: "NORMAL",
                Description: p.Definition.Summary,
                IsActive: true))
            .ToList();

    public static IReadOnlyList<CatalogSearchHit> FilterProcedureHits(
        IReadOnlyList<RegisteredProcedure> visibleProcedures,
        string? domain,
        string? trimmedQuery) =>
        visibleProcedures
            .Where(p => string.IsNullOrWhiteSpace(domain) || string.Equals(p.Definition.CatalogDomain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(p => string.IsNullOrWhiteSpace(trimmedQuery) ||
                        p.Definition.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                        p.Definition.Summary.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            .Select(p => new CatalogSearchHit(
                TableIdentifier: new TableIdentifier(p.Definition.CatalogDomain, "procedure", p.Definition.Name),
                DisplayName: p.Definition.Summary,
                Description: p.Definition.Description ?? p.Definition.Summary,
                Domain: p.Definition.CatalogDomain,
                Sensitivity: "NORMAL",
                CombinedScore: 1.0,
                Bm25Score: 1.0,
                VectorScore: null,
                MatchedTerms: string.IsNullOrWhiteSpace(trimmedQuery) ? [] : [trimmedQuery],
                RelevantColumns: p.Definition.Parameters.Select(param => param.Name).ToList(),
                RelatedJoinPaths: [],
                SuggestedGraphQlField: null,
                Kind: "procedure",
                DatasetId: $"procedure:{p.Definition.Name}",
                AccessStatus: CatalogAccessStatus.Granted))
            .ToList();
}
