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
/// unrepresentable: the constructor rejects it, so a channel can never receive a "hidden" row (issue #138 INC-1, SEC-CAT138-15).
/// </summary>
public readonly record struct CatalogVisibleRow
{
    public CatalogVisibleRow(TableMetadata table, CatalogOutputShape shape)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (shape is not (CatalogOutputShape.Summary or CatalogOutputShape.Full))
        {
            throw new ArgumentOutOfRangeException(nameof(shape), "A visible row is Summary or Full, never NotFound.");
        }

        Table = table;
        Shape = shape;
    }

    public TableMetadata Table { get; }

    public CatalogOutputShape Shape { get; }
}

/// <summary>The discovery-visible tables of one caller, already shaped (NotFound rows dropped) and ReBAC-intersected.</summary>
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
/// The only discovery visibility entry point (always the discovery surface): caller scope, consent and tier engine,
/// shaping and ReBAC in one place. Every failure answers empty / null, never an unfiltered fallback.
/// </summary>
public interface ICatalogVisibilityService
{
    ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default);

    /// <summary>Null = unknown, inactive, hidden or ReBAC-denied (indistinguishable).</summary>
    ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default);

    ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default);
}
