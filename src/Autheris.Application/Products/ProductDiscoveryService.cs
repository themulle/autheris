namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>What the caller's access decision on the product object allows: whether the product is readable and the state of each column.</summary>
public sealed record ProductAccessView(bool Granted, IReadOnlyDictionary<string, string> ColumnStates);

/// <summary>The decision of the caller on the catalog object of a product (the grant of the product, never a base object).</summary>
public interface IProductReadAccess
{
    Task<ProductAccessView> ViewAsync(SecurityPrincipalContext caller, DataProduct product, CancellationToken ct = default);
}

public sealed class ProductReadAccess(ITableMetadataRepository tables, IAccessDecisionPoint decisions) : IProductReadAccess
{
    private static readonly ProductAccessView Denied = new(false, new Dictionary<string, string>());

    public async Task<ProductAccessView> ViewAsync(SecurityPrincipalContext caller, DataProduct product, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(product);
        var meta = await tables.GetTableMetadataAsync(ProductSource.ObjectOf(product.Tenant, product.Schema, product.Name), ct).ConfigureAwait(false);
        if (meta is null || !meta.Table.IsActive)
        {
            return Denied;
        }

        var decision = await decisions.DecideAsync(new AccessRequest(caller, meta, AccessOperation.Read, AccessTransport.Rest), ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return Denied;
        }

        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in product.Columns)
        {
            states[column.Name] = decision.ColumnAccess.TryGetValue(column.Name, out var level) ? StateOf(level) : "denied";
        }

        return new ProductAccessView(true, states);
    }

    private static string StateOf(ColumnAccessLevel level) => level switch
    {
        ColumnAccessLevel.Clear => "clear",
        ColumnAccessLevel.Mask => "masked",
        ColumnAccessLevel.RowDependent => "row-dependent",
        _ => "denied"
    };
}

public sealed record ProductDiscoveryColumn(string Name, string DataType, string Class, string State);

/// <summary>One product as a discovery caller sees it. No base object, SQL or lineage.</summary>
public sealed record ProductDiscoveryItem(
    string Schema,
    string Name,
    string Kind,
    string Status,
    string LoadMode,
    string? Description,
    string? OwnerGroupSid,
    DateTimeOffset? RefreshedAt,
    IReadOnlyList<ProductDiscoveryColumn> Columns);

public sealed record ProductExplainColumn(string Name, string Class, string State);

/// <summary>
/// The explanation of the caller's own access to a product: the product grant decides and base rules do not apply. Base objects, the
/// definition hash and the lineage are filled only for the product owner, the creator, a data product admin or an auditor. It carries
/// no values and no source error text.
/// </summary>
public sealed record ProductExplanation(
    string Schema,
    string Name,
    string Kind,
    bool Allowed,
    bool BaseRulesApply,
    string DecidedBy,
    string LoadMode,
    string Freshness,
    IReadOnlyList<ProductExplainColumn> Columns,
    IReadOnlyList<string>? BaseObjects,
    string? DefinitionHash,
    IReadOnlyList<ColumnLineage>? Lineage);

public sealed record ProductDiscoveryPage(IReadOnlyList<ProductDiscoveryItem> Items, string? NextAfter);

/// <summary>
/// Discovery and explain of data products, scoped to the caller. A product is visible to a caller who holds a read decision on its product
/// object, or who administers it (owner, creator, admin, auditor); a hidden and an unknown product answer with the same not-found error.
/// </summary>
public sealed class ProductDiscoveryService(
    IDataProductRepository products,
    IProductReadAccess access,
    IProductRefreshTimes refreshTimes,
    TimeProvider? timeProvider = null,
    ProductUsageTracker? usage = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ProductDiscoveryPage> ListAsync(
        SecurityPrincipalContext security, ProductCaller caller, string? afterKey, int limit, CancellationToken ct = default)
    {
        var size = Math.Clamp(limit, 1, 200);
        var page = await products.ListProductsAsync(caller.Tenant, afterKey, size, ct).ConfigureAwait(false);
        var items = new List<ProductDiscoveryItem>(page.Count);
        foreach (var product in page)
        {
            var privileged = IsPrivileged(caller, product);
            if (product.Tenant != caller.Tenant || (!privileged && product.Status is DataProductStatus.Retired or DataProductStatus.Invalid))
            {
                continue;
            }

            var view = await access.ViewAsync(security, product, ct).ConfigureAwait(false);
            if (!view.Granted && !privileged)
            {
                continue;
            }

            items.Add(new ProductDiscoveryItem(
                product.Schema, product.Name, product.Kind.ToString(), product.Status.ToString(), product.Load.Mode.ToString(), product.Description,
                product.OwnerGroupSid?.Value, refreshTimes.LoadedAt(product),
                [.. product.Columns.Select(c => new ProductDiscoveryColumn(c.Name, c.DataType, c.Class.ToString(), StateOf(view, c.Name)))]));
        }

        // the cursor follows the unfiltered page, so a page of hidden products does not end the listing
        return new ProductDiscoveryPage(items, page.Count < size ? null : $"{page[^1].Schema}/{page[^1].Name}");
    }

    /// <summary>The usage counters of a product the caller may see (same visibility and not-found answer as explain).</summary>
    public async Task<ProductUsageSnapshot> UsageAsync(
        SecurityPrincipalContext security, ProductCaller caller, string schema, string name, CancellationToken ct = default)
    {
        var explained = await ExplainAsync(security, caller, schema, name, ct).ConfigureAwait(false);
        var product = await products.GetProductAsync(caller.Tenant, explained.Schema, explained.Name, ct).ConfigureAwait(false)
            ?? throw NotFound();
        return usage?.Snapshot(caller.Tenant, product.Id) ?? new ProductUsageSnapshot(0, 0, 0, 0, 0, 0, null);
    }

    public async Task<ProductExplanation> ExplainAsync(
        SecurityPrincipalContext security, ProductCaller caller, string schema, string name, CancellationToken ct = default)
    {
        var product = await products.GetProductAsync(caller.Tenant, schema.Trim().ToLowerInvariant(), name.Trim().ToLowerInvariant(), ct).ConfigureAwait(false);
        var privileged = product is not null && IsPrivileged(caller, product);
        if (product is null || product.Tenant != caller.Tenant)
        {
            throw NotFound();
        }

        var view = await access.ViewAsync(security, product, ct).ConfigureAwait(false);
        var readable = view.Granted && product.Status is not (DataProductStatus.Retired or DataProductStatus.Invalid);
        if (!readable && !privileged)
        {
            throw NotFound();
        }

        var loadedAt = refreshTimes.LoadedAt(product);
        return new ProductExplanation(
            product.Schema, product.Name, product.Kind.ToString(), view.Granted, BaseRulesApply: false,
            DecidedBy: "product-grant", product.Load.Mode.ToString(), ProductFreshness.Evaluate(product, loadedAt, _time.GetUtcNow()).ToString(),
            [.. product.Columns.Select(c => new ProductExplainColumn(c.Name, c.Class.ToString(), StateOf(view, c.Name)))],
            privileged ? [.. product.BaseObjects.Select(b => b.ToQualifiedName())] : null,
            privileged ? product.DefinitionHash : null,
            privileged ? [.. product.Columns.SelectMany(c => c.Lineage)] : null);
    }

    private static string StateOf(ProductAccessView view, string column) => view.ColumnStates.TryGetValue(column, out var s) ? s : "denied";

    private static bool IsPrivileged(ProductCaller caller, DataProduct product) =>
        caller.Tenant == product.Tenant &&
        (caller.CanReadAll || ProductAuthorityService.IsProductOwner(caller, product) ||
         string.Equals(caller.Sid.Value, product.CreatedBy.Value, StringComparison.OrdinalIgnoreCase));

    private static ProductException NotFound() => new(ProductFailure.NotFound, ProductErrorCodes.NotFound);
}
