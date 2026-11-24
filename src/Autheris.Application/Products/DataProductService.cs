namespace Autheris.Application.Products;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;

/// <summary>
/// A create or definition change (PUT). Admin and sync creations must name <see cref="OwnerGroupSid"/>. A <see cref="DryRun"/> runs every
/// check, including the creation authority, and returns the product that would be stored without writing, notifying or auditing it.
/// </summary>
public sealed record ProductPutRequest(
    ProductCaller Caller,
    string Schema,
    string Name,
    DataProductKind Kind,
    string Definition,
    Sid? OwnerGroupSid = null,
    LoadSettings? Load = null,
    ProductLimits? Limits = null,
    string? Description = null,
    bool DryRun = false,
    ProtectionClass? DefaultClass = null,
    CatalogVisibilityMode? Visibility = null);

/// <summary>
/// Administration of data products: creation authority, container owner-group rules, base-owner notification and column classification.
/// Every mutation is audited before it is reported as done; refused operations are audited as denials.
/// </summary>
public sealed partial class DataProductService(
    IDataProductRepository products,
    ProductAuthorityService authority,
    IProductCatalogColumns catalog,
    ISqlEngine sqlEngine,
    IContainerOwnership ownership,
    IAuditWriter audit,
    IConsentEventOutbox? outbox = null,
    TimeProvider? timeProvider = null,
    ILogger<DataProductService>? logger = null,
    IProductOwnershipSync? ownershipSync = null,
    IProductCatalogStore? catalogStore = null,
    ViewDowngradeGuard? downgradeGuard = null,
    IAccessPolicySettingsProvider? policyProvider = null,
    IGovernanceCacheInvalidator? cacheInvalidator = null)
{
    private readonly IProductCatalogStore? _catalogStore = catalogStore;
    private readonly IProductOwnershipSync? _ownershipSync = ownershipSync;
    private readonly ViewDowngradeGuard? _downgradeGuard = downgradeGuard;
    private readonly IAccessPolicySettingsProvider? _policyProvider = policyProvider;
    private readonly IGovernanceCacheInvalidator? _cacheInvalidator = cacheInvalidator;
    private readonly IDataProductRepository _products = products ?? throw new ArgumentNullException(nameof(products));
    private readonly ProductAuthorityService _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    private readonly IProductCatalogColumns _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ISqlEngine _sqlEngine = sqlEngine ?? throw new ArgumentNullException(nameof(sqlEngine));
    private readonly IContainerOwnership _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    private readonly IAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly IConsentEventOutbox? _outbox = outbox;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger? _logger = logger;

    public async Task<DataProduct> GetAsync(ProductCaller caller, string schema, string name, CancellationToken ct = default)
    {
        var product = await _products.GetProductAsync(caller.Tenant, Normalize(schema), Normalize(name), ct).ConfigureAwait(false);
        return product is not null && CanView(caller, product) ? product : throw new ProductException(ProductFailure.NotFound, ProductErrorCodes.NotFound);
    }

    public async Task<IReadOnlyList<DataProduct>> ListAsync(ProductCaller caller, string? afterKey, int limit, CancellationToken ct = default)
    {
        var page = await _products.ListProductsAsync(caller.Tenant, afterKey, Math.Clamp(limit, 1, 200), ct).ConfigureAwait(false);
        return page.Where(p => CanView(caller, p)).ToList();
    }

    /// <summary>Creates the product or changes its definition; the caller needs creation authority over every old and new base object.</summary>
    public async Task<DataProduct> PutAsync(ProductPutRequest command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var caller = command.Caller;
        var (schema, name) = (Normalize(command.Schema), Normalize(command.Name));
        if (!ValidSchema(schema) || !ValidName(name) || command.Description is { Length: > MaxDescriptionLength })
        {
            throw Invalid();
        }

        var load = ValidatedLoad(command.Kind, command.Load);
        var limits = ValidatedLimits(command.Limits);
        if (command.DefaultClass is { } requestedClass && !Enum.IsDefined(requestedClass))
        {
            throw Invalid();
        }

        if (command.Visibility is { } requestedVisibility && !Enum.IsDefined(requestedVisibility))
        {
            throw Invalid();
        }

        var existing = await _products.GetProductAsync(caller.Tenant, schema, name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Status == DataProductStatus.Retired)
            {
                throw new ProductException(ProductFailure.Conflict, ProductErrorCodes.Retired);
            }

            if (existing.Kind != command.Kind)
            {
                throw Invalid();
            }

            if (existing.ManagedBy is not null && !caller.ViaSyncPath)
            {
                throw await DenyAsync(caller, schema, name, ProductErrorCodes.ManagedByGit, ProductFailure.Conflict, ct).ConfigureAwait(false);
            }
        }

        var analysis = await AnalyzeAsync(caller.Tenant, command.Kind, command.Definition, ct).ConfigureAwait(false);
        var authorityObjects = existing is null ? analysis.BaseObjects : analysis.BaseObjects.Concat(existing.BaseObjects).Distinct().ToList();
        var decision = await _authority.AuthorizeCreationAsync(
            caller, command.Kind, authorityObjects, existing?.OwnerGroupSid ?? command.OwnerGroupSid, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            var failure = decision.ErrorCode == ProductErrorCodes.DefinitionInvalid ? ProductFailure.Invalid : ProductFailure.Forbidden;
            throw await DenyAsync(caller, schema, name, decision.ErrorCode!, failure, ct).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        var hash = ProductDefinitions.Hash(command.Definition);
        // A declassification belongs to one definition: any change of the definition falls back to the lineage default.
        var columns = ProductColumnClassifier.Classify(analysis.Columns, analysis.IsPii, existing is not null && existing.DefinitionHash == hash ? existing.Columns : null);
        var visibility = command.Visibility ?? existing?.Visibility ?? await ResolveDefaultVisibilityAsync(caller.Tenant, ct).ConfigureAwait(false);
        var defaultClass = command.DefaultClass ?? existing?.DefaultClass;
        var product = new DataProduct(
            existing?.Id ?? Guid.NewGuid(), caller.Tenant, schema, name, command.Kind, command.Definition.Trim(), hash, analysis.BaseObjects, columns,
            existing?.CreatedBy ?? caller.Sid, existing?.Authority ?? decision.Authority!.Value, DataProductStatus.Active,
            existing?.ManagedBy ?? (caller.ViaSyncPath ? "Git" : null), load, limits, command.Description ?? existing?.Description,
            existing?.OwnerGroupSid ?? decision.OwnerGroupSid, defaultClass, visibility,
            existing is null ? 1 : existing.Version + 1,
            existing?.CreatedAt ?? now, now);
        if (command.DryRun)
        {
            return product;
        }

        var revision = new DataProductVersion(product.Id, product.Version, hash, product.Definition, caller.Sid, decision.Authority!.Value, now);

        var definitionChanged = existing is null || existing.DefinitionHash != hash || !SameObjects(existing.BaseObjects, analysis.BaseObjects);
        var outcome = existing is null
            ? await _products.CreateProductAsync(product, revision, ct).ConfigureAwait(false)
            : await _products.ReplaceProductAsync(product, existing.Version, definitionChanged ? revision : null, ct).ConfigureAwait(false);
        if (outcome.Status is not (ProductWriteStatus.Created or ProductWriteStatus.Replaced))
        {
            throw new ProductException(ProductFailure.Conflict, ProductErrorCodes.Conflict);
        }

        var stored = outcome.Product ?? product;
        await PersistCatalogAsync(stored, ct).ConfigureAwait(false);
        if (_ownershipSync is not null && (existing is null || existing.OwnerGroupSid != stored.OwnerGroupSid))
        {
            await _ownershipSync.SyncAsync(stored, ct).ConfigureAwait(false);
        }

        _cacheInvalidator?.InvalidateTenant(caller.Tenant.Value);
        var notified = definitionChanged
            ? await NotifyBaseOwnersAsync(stored, existing is null ? ProductEventTypes.Created : ProductEventTypes.Changed, now, ct).ConfigureAwait(false)
            : 0;
        await AuditAsync(
            existing is null ? AuditEventTypes.ProductCreated : AuditEventTypes.ProductDefinitionChanged,
            caller, stored, new { authority = decision.Authority.ToString(), baseOwnersNotified = notified, definitionChanged }, now, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Retires the product (it stays in the store as a tombstone). Allowed for owner-group members and creation-authority holders.</summary>
    public async Task<DataProduct> RetireAsync(ProductCaller caller, string schema, string name, CancellationToken ct = default)
    {
        var product = await LoadAsync(caller, schema, name, ct).ConfigureAwait(false);
        if (product.Status == DataProductStatus.Retired)
        {
            throw new ProductException(ProductFailure.Conflict, ProductErrorCodes.Retired);
        }

        var isOwner = await _authority.IsProductOwnerAsync(caller, product, ct).ConfigureAwait(false);
        if (product.ManagedBy is not null && !caller.ViaSyncPath && !isOwner)
        {
            throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.ManagedByGit, ProductFailure.Conflict, ct).ConfigureAwait(false);
        }

        if (!isOwner && !await HasDefinitionAuthorityAsync(caller, product, ct).ConfigureAwait(false))
        {
            throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.CreationNotAuthorized, ProductFailure.Forbidden, ct).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        var retired = await ReplaceAsync(product with { Status = DataProductStatus.Retired, UpdatedAt = now, Version = product.Version + 1 }, product.Version, ct).ConfigureAwait(false);
        await PersistCatalogAsync(retired, ct).ConfigureAwait(false);
        if (_ownershipSync is not null)
        {
            await _ownershipSync.SyncAsync(retired, ct).ConfigureAwait(false);
        }

        _cacheInvalidator?.InvalidateTenant(caller.Tenant.Value);
        var notified = await NotifyBaseOwnersAsync(retired, ProductEventTypes.Retired, now, ct).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.ProductRetired, caller, retired, new { baseOwnersNotified = notified }, now, ct).ConfigureAwait(false);
        return retired;
    }

    /// <summary>Assigns or replaces the container owner group; only <c>DataProductAdmin</c> or Data Governance, never to caller's own SID.</summary>
    public async Task<DataProduct> SetOwnerGroupAsync(ProductCaller caller, string schema, string name, Sid newOwnerGroupSid, CancellationToken ct = default)
    {
        var product = await LoadAsync(caller, schema, name, ct).ConfigureAwait(false);
        var decision = _authority.AuthorizeOwnerGroupChange(caller, product, newOwnerGroupSid);
        if (!decision.Allowed)
        {
            var failure = decision.ErrorCode == ProductErrorCodes.DefinitionInvalid ? ProductFailure.Invalid : ProductFailure.Forbidden;
            throw await DenyAsync(caller, product.Schema, product.Name, decision.ErrorCode!, failure, ct).ConfigureAwait(false);
        }

        if (product.Status == DataProductStatus.Retired)
        {
            throw new ProductException(ProductFailure.Conflict, ProductErrorCodes.Retired);
        }

        var now = _time.GetUtcNow();
        var updated = await ReplaceAsync(
            product with { OwnerGroupSid = decision.OwnerGroupSid, UpdatedAt = now, Version = product.Version + 1 },
            product.Version, ct).ConfigureAwait(false);
        await PersistCatalogAsync(updated, ct).ConfigureAwait(false);
        if (_ownershipSync is not null)
        {
            await _ownershipSync.SyncAsync(updated, ct).ConfigureAwait(false);
        }

        _cacheInvalidator?.InvalidateTenant(caller.Tenant.Value);
        await AuditAsync(
            AuditEventTypes.OwnerGroupAssigned,
            caller,
            updated,
            new { previousOwnerGroupSid = product.OwnerGroupSid?.Value, newOwnerGroupSid = updated.OwnerGroupSid?.Value },
            now,
            ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>The owner group changes the description and the load mode; the definition stays with the creation-authority holders.</summary>
    public async Task<DataProduct> UpdateSettingsAsync(ProductCaller caller, string schema, string name, string? description, LoadSettings? load, CancellationToken ct = default)
    {
        var product = await LoadAsync(caller, schema, name, ct).ConfigureAwait(false);
        if (!await _authority.IsProductOwnerAsync(caller, product, ct).ConfigureAwait(false))
        {
            throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.OwnerOnlyOperation, ProductFailure.Forbidden, ct).ConfigureAwait(false);
        }

        if (product.Status == DataProductStatus.Retired)
        {
            throw new ProductException(ProductFailure.Conflict, ProductErrorCodes.Retired);
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            throw Invalid();
        }

        var now = _time.GetUtcNow();
        var updated = await ReplaceAsync(
            product with { Description = description ?? product.Description, Load = load is null ? product.Load : ValidatedLoad(product.Kind, load), UpdatedAt = now, Version = product.Version + 1 },
            product.Version, ct).ConfigureAwait(false);
        await PersistCatalogAsync(updated, ct).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.ProductSettingsChanged, caller, updated, new { loadMode = updated.Load.Mode.ToString() }, now, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Changes the class of one column. Downgrades from stricter PII classes go through <see cref="ViewDowngradeGuard"/> (DG or
    /// container owner group, IT refused, justification >= 10 chars, audited <c>VIEW_DOWNGRADED</c>).
    /// </summary>
    public async Task<DataProduct> ClassifyColumnAsync(
        ProductCaller caller, string schema, string name, string column, ProductColumnClass target, string? reason, CancellationToken ct = default)
    {
        var product = await LoadAsync(caller, schema, name, ct).ConfigureAwait(false);
        if (product.ManagedBy is not null && !caller.ViaSyncPath)
        {
            throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.ManagedByGit, ProductFailure.Conflict, ct).ConfigureAwait(false);
        }

        var index = product.Columns.ToList().FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new ProductException(ProductFailure.NotFound, ProductErrorCodes.NotFound);
        }

        var existingColumn = product.Columns[index];
        DataProductColumn classifiedColumn;
        if (!caller.ViaSyncPath && ViewDowngradeGuard.IsDowngrade(existingColumn.Class, target) && _downgradeGuard is not null)
        {
            var principal = caller.PrincipalContext
                            ?? throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.CreationNotAuthorized, ProductFailure.Forbidden, ct).ConfigureAwait(false);
            classifiedColumn = await _downgradeGuard.GuardAndAuditAsync(principal, product, existingColumn, target, reason, ct).ConfigureAwait(false);
        }
        else
        {
            if (!await HasDefinitionAuthorityAsync(caller, product, ct).ConfigureAwait(false))
            {
                throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.CreationNotAuthorized, ProductFailure.Forbidden, ct).ConfigureAwait(false);
            }

            var result = ProductColumnClassifier.Declassify(existingColumn, target, reason, caller.Sid);
            if (result.Column is null)
            {
                throw await DenyAsync(caller, product.Schema, product.Name, result.ErrorCode!, ProductFailure.Invalid, ct).ConfigureAwait(false);
            }

            classifiedColumn = result.Column;
        }

        var columns = product.Columns.ToList();
        columns[index] = classifiedColumn;
        var now = _time.GetUtcNow();
        var updated = await ReplaceAsync(product with { Columns = columns, UpdatedAt = now, Version = product.Version + 1 }, product.Version, ct).ConfigureAwait(false);
        await PersistCatalogAsync(updated, ct).ConfigureAwait(false);
        _cacheInvalidator?.InvalidateTenant(caller.Tenant.Value);
        await AuditAsync(
            AuditEventTypes.ProductColumnClassificationChanged, caller, updated,
            new { column = classifiedColumn.Name, from = existingColumn.Class.ToString(), to = target.ToString(), reason = classifiedColumn.DeclassificationReason }, now, ct).ConfigureAwait(false);
        return updated;
    }
}
