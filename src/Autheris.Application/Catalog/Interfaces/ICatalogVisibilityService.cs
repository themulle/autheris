namespace Autheris.Application.Catalog.Interfaces;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Diagnostics only: which channel asked. It never changes a result and never carries caller values.</summary>
public enum CatalogChannel
{
    Rest = 0,
    GraphQl = 1,
    Mcp = 2
}

/// <summary>The caller of a discovery request. <see cref="Tenant"/> is supplied by the channel exactly as before (never wider).</summary>
public sealed record CatalogCaller(ClaimsPrincipal? Principal, TenantId Tenant, CatalogChannel Channel);

/// <summary>
/// A visible catalog row together with the output shape the channel must apply. <see cref="CatalogOutputShape.NotFound"/> is
/// unrepresentable: the constructor rejects it, so a channel can never receive a "hidden" row (issue #138 INC-1).
/// </summary>
public readonly record struct CatalogVisibleRow
{
    public CatalogVisibleRow(TableMetadata table, CatalogOutputShape shape)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (shape != CatalogOutputShape.Full)
        {
            throw new ArgumentOutOfRangeException(nameof(shape), "A visible row is Full, never NotFound.");
        }

        Table = table;
        Shape = shape;
    }

    public TableMetadata Table { get; }

    public CatalogOutputShape Shape { get; }
}

/// <summary>The discovery-visible tables of one caller, already shaped (NotFound rows dropped).</summary>
public sealed class CatalogVisibleSet
{
    private readonly Dictionary<TableIdentifier, int> _index = [];

    internal CatalogVisibleSet(
        IReadOnlyList<TableMetadata> tables, CallerScope scope, bool isAdminView, IReadOnlyList<TableMetadata>? allTables = null)
    {
        Tables = tables;
        Scope = scope;
        IsAdminView = isAdminView;
        AllTables = allTables ?? [];
        for (var i = 0; i < tables.Count; i++)
        {
            _index[tables[i].Identifier] = i;
        }
    }

    public static CatalogVisibleSet Empty { get; } = new([], CallerScope.None, isAdminView: false);

    public IReadOnlyList<TableMetadata> Tables { get; }

    public CallerScope Scope { get; }

    /// <summary>The <see cref="CallerScopeResolver.IsAdminView"/> result of the one resolution that produced <see cref="Tables"/>.</summary>
    public bool IsAdminView { get; }

    /// <summary>Unfiltered active catalog; same-assembly use only (search hygiene), never put on the wire.</summary>
    internal IReadOnlyList<TableMetadata> AllTables { get; }

    public bool Contains(TableIdentifier id) => _index.ContainsKey(id);

    public TableMetadata? Find(TableIdentifier id) => _index.TryGetValue(id, out var i) ? Tables[i] : null;

    /// <summary>Shape for a visible table; <see cref="CatalogOutputShape.NotFound"/> for an unknown or hidden one (fail closed).</summary>
    public CatalogOutputShape ShapeOf(TableIdentifier id) =>
        Find(id) is { } row ? CatalogViewShaping.Resolve(IsAdminView, row.CatalogView) : CatalogOutputShape.NotFound;
}

/// <summary>
/// Result of <see cref="ICatalogVisibilityService.FindVisibleManyAsync"/>. <c>default</c> has <see cref="IsAuthoritative"/> = <c>false</c>
/// so unconfigured test doubles cleanly trigger fallback to individual visibility lookups.
/// </summary>
public readonly record struct CatalogVisibleBatch(
    bool IsAuthoritative,
    IReadOnlyDictionary<TableIdentifier, CatalogVisibleRow> Rows)
{
    private static readonly IReadOnlyDictionary<TableIdentifier, CatalogVisibleRow> EmptyRows =
        new Dictionary<TableIdentifier, CatalogVisibleRow>();

    public static CatalogVisibleBatch Empty { get; } = new(true, EmptyRows);

    public static CatalogVisibleBatch Of(IReadOnlyDictionary<TableIdentifier, CatalogVisibleRow>? rows) =>
        new(true, rows ?? EmptyRows);
}

/// <summary>
/// The only discovery visibility entry point (always the discovery surface): caller scope, consent and tier engine,
/// and shaping in one place. Every failure answers empty / null, never an unfiltered fallback.
/// </summary>
public interface ICatalogVisibilityService
{
    ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default);

    /// <summary>Null = unknown, inactive, hidden or denied (indistinguishable).</summary>
    ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default);

    ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default);

    /// <summary>
    /// Evaluates visibility and column shaping for a batch of <paramref name="candidates"/> in one pass so callers can resolve both
    /// target dataset existence and target visible columns without repeating per-target visibility queries.
    /// </summary>
    async ValueTask<CatalogVisibleBatch> FindVisibleManyAsync(
        CatalogCaller caller,
        IReadOnlyCollection<TableIdentifier> candidates,
        CancellationToken ct = default)
    {
        if (candidates is null or { Count: 0 })
        {
            return CatalogVisibleBatch.Empty;
        }

        var rows = new Dictionary<TableIdentifier, CatalogVisibleRow>();
        foreach (var candidate in candidates)
        {
            if (!rows.ContainsKey(candidate) && await FindVisibleAsync(caller, candidate, ct).ConfigureAwait(false) is { } visible)
            {
                rows[candidate] = visible;
            }
        }

        return CatalogVisibleBatch.Of(rows);
    }
}
