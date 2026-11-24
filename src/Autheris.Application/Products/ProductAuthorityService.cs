namespace Autheris.Application.Products;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

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
    public const string CatalogPersistFailed = "PRODUCT_CATALOG_PERSIST_FAILED";
    public const string LoadInProgress = "PRODUCT_LOAD_IN_PROGRESS";
    public const string LimitExceeded = "PRODUCT_LIMIT_EXCEEDED";
    public const string DefinitionChanged = "PRODUCT_DEFINITION_CHANGED";
    public const string BaseObjectInactive = "PRODUCT_BASE_OBJECT_INACTIVE";
}

/// <summary>The caller of a product operation. <see cref="ViaSyncPath"/> is set only by the signed sync path, never by a REST route.</summary>
public sealed record ProductCaller(
    Sid Sid,
    TenantId Tenant,
    bool IsDataProductAdmin,
    bool ViaSyncPath = false,
    bool CanReadAll = false,
    SecurityPrincipalContext? PrincipalContext = null);

public enum ProductFailure { Forbidden, NotFound, Conflict, Invalid }

/// <summary>A refused product operation: a stable <see cref="Code"/> and the kind of failure; the message never carries caller input.</summary>
public sealed class ProductException(ProductFailure failure, string code) : Exception(code)
{
    public ProductFailure Failure { get; } = failure;

    public string Code { get; } = code;
}

public sealed record ProductAuthorityDecision(bool Allowed, ProductCreatorAuthority? Authority, Sid? OwnerGroupSid, string? ErrorCode)
{
    public static ProductAuthorityDecision Allow(ProductCreatorAuthority authority, Sid? ownerGroupSid) => new(true, authority, ownerGroupSid, null);

    public static ProductAuthorityDecision Deny(string code) => new(false, null, null, code);
}

/// <summary>
/// Creation authority and container owner-group rules for data products: ownership of every base object (via container owner-group
/// membership or direct ownership), <c>DataProductAdmin</c> / Data Governance, or Git sync. Base owners approve nothing; they are only notified.
/// </summary>
public sealed class ProductAuthorityService(
    IContainerOwnership ownership,
    IDataProductRepository products,
    TimeProvider? timeProvider = null,
    IGovernanceCapabilities? capabilities = null,
    IGovernanceDomainRepository? domains = null,
    ITableMetadataRepository? tables = null)
{
    private readonly IContainerOwnership _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    private readonly IDataProductRepository _products = products ?? throw new ArgumentNullException(nameof(products));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IGovernanceCapabilities? _capabilities = capabilities;
    private readonly IGovernanceDomainRepository? _domains = domains;
    private readonly ITableMetadataRepository? _tables = tables;

    /// <summary>
    /// The caller of a REST route, or null when the principal holds the sync role: that identity may only call the sync endpoints
    /// and is never accepted on an administration route, even when it would also own objects.
    /// </summary>
    public static ProductCaller? ForRestRoute(
        Sid sid,
        TenantId tenant,
        bool isDataProductAdmin,
        bool isDataProductSync,
        bool canReadAll = false,
        SecurityPrincipalContext? principalContext = null) =>
        isDataProductSync ? null : new ProductCaller(sid, tenant, isDataProductAdmin, false, canReadAll || isDataProductAdmin, principalContext);

    /// <summary>
    /// Decides who may create a product over <paramref name="baseObjects"/> and resolves the container's <c>OwnerGroupSid</c>.
    /// </summary>
    public async Task<ProductAuthorityDecision> AuthorizeCreationAsync(
        ProductCaller caller,
        DataProductKind kind,
        IReadOnlyList<TableIdentifier> baseObjects,
        Sid? namedOwnerGroup,
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
            return OwnerMustDiffer(caller, namedOwnerGroup, ProductCreatorAuthority.GitSync);
        }

        // The objects a procedure reads or writes cannot be verified: ownership of base objects never suffices for a procedure.
        if (kind == DataProductKind.View)
        {
            var (allOwned, inheritedGroup) = await ResolveBaseOwnershipAsync(caller, baseObjects, ct).ConfigureAwait(false);
            if (allOwned)
            {
                if (namedOwnerGroup is { } explicitSid)
                {
                    if (IsBlank(explicitSid))
                    {
                        return ProductAuthorityDecision.Deny(ProductErrorCodes.OwnerMustDiffer);
                    }

                    if (!SidFormat.IsCanonical(explicitSid.Value.Trim()))
                    {
                        return ProductAuthorityDecision.Deny(ProductErrorCodes.DefinitionInvalid);
                    }

                    return ProductAuthorityDecision.Allow(ProductCreatorAuthority.BaseOwner, new Sid(explicitSid.Value.Trim()));
                }

                return ProductAuthorityDecision.Allow(ProductCreatorAuthority.BaseOwner, inheritedGroup ?? caller.Sid);
            }
        }

        if (caller.IsDataProductAdmin || IsGovernanceCaller(caller, caller.Tenant))
        {
            return OwnerMustDiffer(caller, namedOwnerGroup, ProductCreatorAuthority.DataProductAdmin);
        }

        return ProductAuthorityDecision.Deny(ProductErrorCodes.CreationNotAuthorized);
    }

    /// <summary>Only <c>DataProductAdmin</c> or Data Governance assigns or replaces the container owner group, never to caller's own SID.</summary>
    public ProductAuthorityDecision AuthorizeOwnerGroupChange(ProductCaller caller, DataProduct product, Sid newOwnerGroupSid)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(product);
        if (caller.ViaSyncPath || caller.Tenant != product.Tenant ||
            (!caller.IsDataProductAdmin && !IsGovernanceCaller(caller, product.Tenant)))
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.CreationNotAuthorized);
        }

        if (IsBlank(newOwnerGroupSid) || SidEquals(newOwnerGroupSid, caller.Sid))
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.OwnerMustDiffer);
        }

        var normalized = newOwnerGroupSid.Value.Trim();
        if (!SidFormat.IsCanonical(normalized))
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.DefinitionInvalid);
        }

        return ProductAuthorityDecision.Allow(product.Authority, new Sid(normalized));
    }

    /// <summary>Determines whether <paramref name="caller"/> is a member of <paramref name="product"/>'s container owner group.</summary>
    public static bool IsProductOwner(ProductCaller caller, DataProduct product)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(product);
        if (caller.Tenant != product.Tenant || caller.ViaSyncPath || product.OwnerGroupSid is not { } ownerGroupSid)
        {
            return false;
        }

        if (caller.PrincipalContext is { } ctx)
        {
            return ctx.GroupResolutionStatus == GroupResolutionStatus.Complete &&
                   ctx.ResolvedTenant == product.Tenant &&
                   ctx.GroupSids.Contains(ownerGroupSid);
        }

        return SidEquals(caller.Sid, ownerGroupSid);
    }

    public async Task<bool> IsProductOwnerAsync(ProductCaller caller, DataProduct product, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(product);
        if (caller.Tenant != product.Tenant || caller.ViaSyncPath || product.OwnerGroupSid is not { } ownerGroupSid)
        {
            return false;
        }

        if (_capabilities is not null && caller.PrincipalContext is { } ctx && ctx.ResolvedTenant == product.Tenant)
        {
            return await _capabilities.IsOwnerMemberAsync(
                ctx,
                new ContainerRef(ContainerKind.DataProduct, product.Id),
                ct).ConfigureAwait(false);
        }

        return IsProductOwner(caller, product);
    }

    private bool IsGovernanceCaller(ProductCaller caller, TenantId tenant) =>
        _capabilities is not null &&
        caller.PrincipalContext?.Principal is not null &&
        _capabilities.IsGovernance(caller.PrincipalContext.Principal, tenant);

    private static ProductAuthorityDecision OwnerMustDiffer(ProductCaller caller, Sid? namedOwner, ProductCreatorAuthority authority)
    {
        if (namedOwner is not { } owner || IsBlank(owner) || SidEquals(owner, caller.Sid))
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.OwnerMustDiffer);
        }

        var normalized = owner.Value.Trim();
        if (!SidFormat.IsCanonical(normalized))
        {
            return ProductAuthorityDecision.Deny(ProductErrorCodes.DefinitionInvalid);
        }

        return ProductAuthorityDecision.Allow(authority, new Sid(normalized));
    }

    private async Task<(bool AllOwned, Sid? InheritedOwnerGroup)> ResolveBaseOwnershipAsync(
        ProductCaller caller,
        IReadOnlyList<TableIdentifier> baseObjects,
        CancellationToken ct)
    {
        var distinct = baseObjects.Distinct().ToList();
        if (_capabilities is not null && _domains is not null && _tables is not null && caller.PrincipalContext is { } ctx)
        {
            if (ctx.GroupResolutionStatus != GroupResolutionStatus.Complete || ctx.ResolvedTenant != caller.Tenant)
            {
                return (false, null);
            }

            var resolvedGroups = new HashSet<Sid>();
            foreach (var table in distinct)
            {
                ContainerDefaults container;
                if (ProductSource.TryParse(table, caller.Tenant, out var prodSchema, out var prodName))
                {
                    var baseProd = await _products.GetProductAsync(caller.Tenant, prodSchema, prodName, ct).ConfigureAwait(false);
                    if (baseProd is null)
                    {
                        return (false, null);
                    }

                    container = new ContainerDefaults(
                        new ContainerRef(ContainerKind.DataProduct, baseProd.Id),
                        baseProd.OwnerGroupSid,
                        baseProd.DefaultClass,
                        baseProd.Visibility,
                        IsValid: true);
                }
                else
                {
                    var meta = await _tables.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
                    if (meta is null || !meta.Table.IsActive)
                    {
                        return (false, null);
                    }

                    container = await _domains.GetContainerForTableAsync(caller.Tenant, meta.Table.Id, ct).ConfigureAwait(false);
                }

                if (!container.IsValid || container.OwnerGroupSid is not { } ownerGroup)
                {
                    return (false, null);
                }

                var isMember = await _capabilities.IsOwnerMemberAsync(ctx, container.Container, ct).ConfigureAwait(false);
                if (!isMember)
                {
                    return (false, null);
                }

                resolvedGroups.Add(ownerGroup);
            }

            return (true, resolvedGroups.Count == 1 ? (Sid?)resolvedGroups.First() : null);
        }

        var owned = await _ownership.GetOwnedOrDelegatedTablesAsync(caller.Sid, distinct, _time.GetUtcNow(), ct).ConfigureAwait(false);
        return (distinct.All(owned.Contains), null);
    }

    private static bool IsBlank(Sid sid) => string.IsNullOrWhiteSpace(sid.Value);

    private static bool SidEquals(Sid a, Sid b) => string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);
}
