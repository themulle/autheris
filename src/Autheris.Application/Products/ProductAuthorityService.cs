namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Stable error codes of the data product rules (mirrored by the API error codes).</summary>
public static class ProductErrorCodes
{
    public const string CreationNotAuthorized = "PRODUCT_CREATION_NOT_AUTHORIZED";
    public const string OwnerMustDiffer = "PRODUCT_OWNER_MUST_DIFFER";
    public const string TransferForbidden = "PRODUCT_TRANSFER_FORBIDDEN";
    public const string OwnerOnlyOperation = "PRODUCT_OWNER_ONLY";
    public const string ManagedByGit = "PRODUCT_MANAGED_BY_GIT";
    public const string NotFound = "PRODUCT_NOT_FOUND";
    public const string DefinitionInvalid = "PRODUCT_DEFINITION_INVALID";
    public const string ReasonRequired = "PRODUCT_REASON_REQUIRED";
    public const string Conflict = "PRODUCT_CONFLICT";
    public const string Retired = "PRODUCT_RETIRED";
    public const string Stale = "PRODUCT_STALE";
    public const string Unavailable = "PRODUCT_UNAVAILABLE";
    public const string LoadFailed = "PRODUCT_LOAD_FAILED";
    public const string OwnerSyncFailed = "PRODUCT_OWNER_SYNC_FAILED";
    public const string LoadInProgress = "PRODUCT_LOAD_IN_PROGRESS";
    public const string LimitExceeded = "PRODUCT_LIMIT_EXCEEDED";
    public const string DefinitionChanged = "PRODUCT_DEFINITION_CHANGED";
}

/// <summary>The caller of a product operation. <see cref="ViaSyncPath"/> is set only by the signed sync path, never by a REST route.</summary>
public sealed record ProductCaller(Sid Sid, TenantId Tenant, bool IsDataProductAdmin, bool ViaSyncPath = false, bool CanReadAll = false);

public enum ProductFailure { Forbidden, NotFound, Conflict, Invalid }

/// <summary>A refused product operation: a stable <see cref="Code"/> and the kind of failure; the message never carries caller input.</summary>
public sealed class ProductException(ProductFailure failure, string code) : Exception(code)
{
    public ProductFailure Failure { get; } = failure;

    public string Code { get; } = code;
}

public sealed record ProductAuthorityDecision(bool Allowed, ProductCreatorAuthority? Authority, Sid? ProductOwner, string? ErrorCode)
{
    public static ProductAuthorityDecision Allow(ProductCreatorAuthority authority, Sid owner) => new(true, authority, owner, null);

    public static ProductAuthorityDecision Deny(string code) => new(false, null, null, code);
}

/// <summary>
/// Creation authority and product-owner rules of data products: ownership of every base object (incl. delegates), ownership of the
/// data source, <c>DataProductAdmin</c> or the Git sync. Base owners approve nothing; they are only notified.
/// </summary>
public sealed class ProductAuthorityService(IDataOwnershipRepository ownership, IDataProductRepository products, TimeProvider? timeProvider = null)
{
    private readonly IDataOwnershipRepository _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    private readonly IDataProductRepository _products = products ?? throw new ArgumentNullException(nameof(products));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The caller of a REST route, or null when the principal holds the sync role: that identity may only call the sync endpoints
    /// and is never accepted on an administration route, even when it would also own objects.
    /// </summary>
    public static ProductCaller? ForRestRoute(Sid sid, TenantId tenant, bool isDataProductAdmin, bool isDataProductSync, bool canReadAll = false) =>
        isDataProductSync ? null : new ProductCaller(sid, tenant, isDataProductAdmin, false, canReadAll || isDataProductAdmin);

    /// <summary>
    /// Decides who may create a product over <paramref name="baseObjects"/> and who becomes its product owner. Ownership-based authorities
    /// make the caller the product owner; admin and sync creations must name a different owner.
    /// </summary>
    public async Task<ProductAuthorityDecision> AuthorizeCreationAsync(
        ProductCaller caller,
        DataProductKind kind,
        IReadOnlyList<TableIdentifier> baseObjects,
        Sid? namedOwner,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(baseObjects);
        if (baseObjects.Count == 0)
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.CreationNotAuthorized);
        }

        if (caller.ViaSyncPath)
        {
            return OwnerMustDiffer(caller, namedOwner, ProductCreatorAuthority.GitSync);
        }

        if (await OwnsEverySourceAsync(caller, baseObjects, ct).ConfigureAwait(false))
        {
            return ProductAuthorityDecision.Allow(ProductCreatorAuthority.DataSourceOwner, caller.Sid);
        }

        // The objects a procedure reads or writes cannot be verified: ownership of the base objects never suffices for a procedure.
        if (kind == DataProductKind.View && await OwnsEveryObjectAsync(caller, baseObjects, ct).ConfigureAwait(false))
        {
            return ProductAuthorityDecision.Allow(ProductCreatorAuthority.BaseOwner, caller.Sid);
        }

        return caller.IsDataProductAdmin
            ? OwnerMustDiffer(caller, namedOwner, ProductCreatorAuthority.DataProductAdmin)
            : ProductAuthorityDecision.Deny(ProductErrorCodes.CreationNotAuthorized);
    }

    /// <summary>Only a <c>DataProductAdmin</c> transfers a product, never to themselves. The sync identity never transfers.</summary>
    public static ProductAuthorityDecision AuthorizeTransfer(ProductCaller caller, DataProduct product, Sid newOwner)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(product);
        if (!caller.IsDataProductAdmin || caller.ViaSyncPath || caller.Tenant != product.Tenant)
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.CreationNotAuthorized);
        }

        if (IsBlank(newOwner) || SidEquals(newOwner, caller.Sid))
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.OwnerMustDiffer);
        }

        return ProductAuthorityDecision.Allow(product.Authority, newOwner);
    }

    /// <summary>
    /// The product owner decides grants, row policies, masking, hidden columns, description and refresh; never the definition, the base
    /// objects or the column classification (those need creation authority, see <see cref="AuthorizeCreationAsync"/>).
    /// </summary>
    public static bool IsProductOwner(ProductCaller caller, DataProduct product) =>
        caller.Tenant == product.Tenant && !caller.ViaSyncPath && SidEquals(caller.Sid, product.ProductOwner);

    private static ProductAuthorityDecision OwnerMustDiffer(ProductCaller caller, Sid? namedOwner, ProductCreatorAuthority authority)
    {
        if (namedOwner is not { } owner || IsBlank(owner) || SidEquals(owner, caller.Sid))
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.OwnerMustDiffer);
        }

        return ProductAuthorityDecision.Allow(authority, owner);
    }

    private async Task<bool> OwnsEverySourceAsync(ProductCaller caller, IReadOnlyList<TableIdentifier> baseObjects, CancellationToken ct)
    {
        foreach (var source in baseObjects.Select(static o => o.Domain).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await _products.IsDataSourceOwnerAsync(caller.Tenant, source, caller.Sid, ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> OwnsEveryObjectAsync(ProductCaller caller, IReadOnlyList<TableIdentifier> baseObjects, CancellationToken ct)
    {
        var distinct = baseObjects.Distinct().ToList();
        var owned = await _ownership.GetOwnedOrDelegatedTablesAsync(caller.Sid, distinct, _time.GetUtcNow(), ct).ConfigureAwait(false);
        return distinct.All(owned.Contains);
    }

    private static bool IsBlank(Sid sid) => string.IsNullOrWhiteSpace(sid.Value);

    private static bool SidEquals(Sid a, Sid b) => string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);
}
