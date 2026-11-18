namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;

/// <summary>A create or definition change (PUT). Admin and sync creations must name <see cref="ProductOwner"/>.</summary>
public sealed record ProductPutRequest(
    ProductCaller Caller,
    string Schema,
    string Name,
    DataProductKind Kind,
    string Definition,
    Sid? ProductOwner = null,
    LoadSettings? Load = null,
    ProductLimits? Limits = null,
    string? Description = null);

/// <summary>
/// Administration of data products: creation authority, product-owner rules, base-owner notification and column classification.
/// Every mutation is audited before it is reported as done; refused operations are audited as denials.
/// </summary>
public sealed partial class DataProductService(
    IDataProductRepository products,
    ProductAuthorityService authority,
    IProductCatalogColumns catalog,
    ISqlEngine sqlEngine,
    IDataOwnershipRepository ownership,
    IAuditWriter audit,
    IConsentEventOutbox? outbox = null,
    TimeProvider? timeProvider = null,
    ILogger<DataProductService>? logger = null,
    IProductOwnershipSync? ownershipSync = null,
    IProductCatalogStore? catalogStore = null)
{
    private readonly IProductCatalogStore? _catalogStore = catalogStore;
    private readonly IProductOwnershipSync? _ownershipSync = ownershipSync;
    private readonly IDataProductRepository _products = products ?? throw new ArgumentNullException(nameof(products));
    private readonly ProductAuthorityService _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    private readonly IProductCatalogColumns _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ISqlEngine _sqlEngine = sqlEngine ?? throw new ArgumentNullException(nameof(sqlEngine));
    private readonly IDataOwnershipRepository _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
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
            caller, command.Kind, authorityObjects, existing?.ProductOwner ?? command.ProductOwner, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw await DenyAsync(caller, schema, name, decision.ErrorCode!, ProductFailure.Forbidden, ct).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        var hash = ProductDefinitions.Hash(command.Definition);
        // A declassification belongs to one definition: any change of the definition falls back to the lineage default.
        var columns = ProductColumnClassifier.Classify(analysis.Columns, analysis.IsPii, existing is not null && existing.DefinitionHash == hash ? existing.Columns : null);
        var product = new DataProduct(
            existing?.Id ?? Guid.NewGuid(), caller.Tenant, schema, name, command.Kind, command.Definition.Trim(), hash, analysis.BaseObjects, columns,
            existing?.CreatedBy ?? caller.Sid, existing?.Authority ?? decision.Authority!.Value, DataProductStatus.Active,
            existing?.ManagedBy ?? (caller.ViaSyncPath ? "Git" : null), load, limits, command.Description ?? existing?.Description,
            existing?.ProductOwner ?? decision.ProductOwner!.Value, existing is null ? 1 : existing.Version + 1,
            existing?.CreatedAt ?? now, now);
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
        if (_ownershipSync is not null && (existing is null || existing.ProductOwner != stored.ProductOwner))
        {
            await _ownershipSync.SyncAsync(stored, ct).ConfigureAwait(false);
        }

        var notified = definitionChanged
            ? await NotifyBaseOwnersAsync(stored, existing is null ? ProductEventTypes.Created : ProductEventTypes.Changed, now, ct).ConfigureAwait(false)
            : 0;
        await AuditAsync(
            existing is null ? AuditEventTypes.ProductCreated : AuditEventTypes.ProductDefinitionChanged,
            caller, stored, new { authority = decision.Authority.ToString(), baseOwnersNotified = notified, definitionChanged }, now, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Retires the product (it stays in the store as a tombstone). Allowed for the product owner and for creation-authority holders.</summary>
    public async Task<DataProduct> RetireAsync(ProductCaller caller, string schema, string name, CancellationToken ct = default)
    {
        var product = await LoadAsync(caller, schema, name, ct).ConfigureAwait(false);
        if (product.Status == DataProductStatus.Retired)
        {
            throw new ProductException(ProductFailure.Conflict, ProductErrorCodes.Retired);
        }

        if (product.ManagedBy is not null && !caller.ViaSyncPath && !ProductAuthorityService.IsProductOwner(caller, product))
        {
            throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.ManagedByGit, ProductFailure.Conflict, ct).ConfigureAwait(false);
        }

        if (!ProductAuthorityService.IsProductOwner(caller, product) && !await HasDefinitionAuthorityAsync(caller, product, ct).ConfigureAwait(false))
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

        var notified = await NotifyBaseOwnersAsync(retired, ProductEventTypes.Retired, now, ct).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.ProductRetired, caller, retired, new { baseOwnersNotified = notified }, now, ct).ConfigureAwait(false);
        return retired;
    }

    /// <summary>Transfers the product to another owner; only a <c>DataProductAdmin</c>, never to themselves.</summary>
    public async Task<DataProduct> TransferAsync(ProductCaller caller, string schema, string name, Sid newOwner, CancellationToken ct = default)
    {
        var product = await LoadAsync(caller, schema, name, ct).ConfigureAwait(false);
        var decision = ProductAuthorityService.AuthorizeTransfer(caller, product, newOwner);
        if (!decision.Allowed)
        {
            throw await DenyAsync(caller, product.Schema, product.Name, decision.ErrorCode!, ProductFailure.Forbidden, ct).ConfigureAwait(false);
        }

        if (product.Status == DataProductStatus.Retired)
        {
            throw new ProductException(ProductFailure.Conflict, ProductErrorCodes.Retired);
        }

        var now = _time.GetUtcNow();
        var moved = await ReplaceAsync(product with { ProductOwner = newOwner, UpdatedAt = now, Version = product.Version + 1 }, product.Version, ct).ConfigureAwait(false);
        await PersistCatalogAsync(moved, ct).ConfigureAwait(false);
        if (_ownershipSync is not null)
        {
            await _ownershipSync.SyncAsync(moved, ct).ConfigureAwait(false);
        }

        await AuditAsync(AuditEventTypes.ProductOwnerTransferred, caller, moved, new { previousOwner = product.ProductOwner.Value, newOwner = newOwner.Value }, now, ct).ConfigureAwait(false);
        return moved;
    }

    /// <summary>The product owner changes the description and the load mode; the definition stays with the creation-authority holders.</summary>
    public async Task<DataProduct> UpdateSettingsAsync(ProductCaller caller, string schema, string name, string? description, LoadSettings? load, CancellationToken ct = default)
    {
        var product = await LoadAsync(caller, schema, name, ct).ConfigureAwait(false);
        if (!ProductAuthorityService.IsProductOwner(caller, product))
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
    /// Changes the class of one column. Only creation-authority holders may (never the product owner); lowering the protection needs a
    /// reason and is audited with the actor.
    /// </summary>
    public async Task<DataProduct> ClassifyColumnAsync(
        ProductCaller caller, string schema, string name, string column, ProductColumnClass target, string? reason, CancellationToken ct = default)
    {
        var product = await LoadAsync(caller, schema, name, ct).ConfigureAwait(false);
        if (product.ManagedBy is not null && !caller.ViaSyncPath)
        {
            throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.ManagedByGit, ProductFailure.Conflict, ct).ConfigureAwait(false);
        }

        if (!await HasDefinitionAuthorityAsync(caller, product, ct).ConfigureAwait(false))
        {
            throw await DenyAsync(caller, product.Schema, product.Name, ProductErrorCodes.CreationNotAuthorized, ProductFailure.Forbidden, ct).ConfigureAwait(false);
        }

        var index = product.Columns.ToList().FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new ProductException(ProductFailure.NotFound, ProductErrorCodes.NotFound);
        }

        var result = ProductColumnClassifier.Declassify(product.Columns[index], target, reason, caller.Sid);
        if (result.Column is null)
        {
            throw await DenyAsync(caller, product.Schema, product.Name, result.ErrorCode!, ProductFailure.Invalid, ct).ConfigureAwait(false);
        }

        var columns = product.Columns.ToList();
        columns[index] = result.Column;
        var now = _time.GetUtcNow();
        var updated = await ReplaceAsync(product with { Columns = columns, UpdatedAt = now, Version = product.Version + 1 }, product.Version, ct).ConfigureAwait(false);
        await PersistCatalogAsync(updated, ct).ConfigureAwait(false);
        await AuditAsync(
            AuditEventTypes.ProductColumnClassificationChanged, caller, updated,
            new { column = result.Column.Name, from = product.Columns[index].Class.ToString(), to = target.ToString(), reason = result.Column.DeclassificationReason }, now, ct).ConfigureAwait(false);
        return updated;
    }
}
