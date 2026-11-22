namespace Autheris.Application.Governance.Domains;

using System.Diagnostics;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Protection;
using Autheris.Application.Products;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed partial class GovernanceDomainService
{
    public async Task<DomainServiceResult<ProtectionWriteOutcome>> SetTableProtectionAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        string datasetId,
        ProtectionClass? requestedClass,
        string? requestedMaskCategory,
        CancellationToken ct = default)
    {
        var meta = await ResolveTenantTableAsync(tenant, datasetId, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return NotFound<ProtectionWriteOutcome>("RESOURCE_NOT_FOUND", "Dataset was not found.");
        }

        var container = await ResolveContainerForTableAsync(tenant, meta, ct).ConfigureAwait(false);
        var containerRef = container.IsValid ? container.Container : ContainerDefaults.Unassigned.Container;
        if (!await _capabilities.CanClassifyAsync(caller, containerRef, tenant, ct).ConfigureAwait(false))
        {
            return Forbidden<ProtectionWriteOutcome>("Only Data Governance or owner members can update object protection.");
        }

        var (beforeTable, beforeCols) = await _domains.GetProtectionAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false);
        var colNames = meta.Columns.Select(c => c.ColumnName).ToList();
        var beforeEffective = EffectiveProtectionResolver.Resolve(container, beforeTable, beforeCols, colNames);

        var tableChange = ProtectionWriteValidator.ApplyTableChange(container.DefaultClass, requestedClass, requestedMaskCategory, beforeCols);
        if (!tableChange.IsValid || tableChange.NormalizedTableProtection is null)
        {
            return Invalid<ProtectionWriteOutcome>(tableChange.ErrorCode ?? ProtectionWriteValidator.InvalidMaskCategoryErrorCode, tableChange.ErrorMessage ?? "Invalid protection parameters.");
        }

        await _domains.SetTableProtectionAsync(
            tenant,
            meta.Table.Id,
            tableChange.NormalizedTableProtection,
            tableChange.ClearedRedundantColumns,
            caller.UserSid,
            ct).ConfigureAwait(false);

        var remainingCols = ApplyClearedColumns(beforeCols, tableChange.ClearedRedundantColumns);
        var afterEffective = EffectiveProtectionResolver.Resolve(container, tableChange.NormalizedTableProtection, remainingCols, colNames);

        await AuditTableProtectionChangeAsync(
            tenant,
            caller.UserSid,
            meta,
            container,
            beforeEffective,
            afterEffective,
            tableChange,
            ct).ConfigureAwait(false);

        _cacheInvalidator?.InvalidateTenant(tenant.Value);
        return Ok(new ProtectionWriteOutcome(meta.Identifier.ToString(), meta.Table.Id, null, afterEffective, LooserThanContainer: tableChange.IsLooserThanContainer));
    }

    public async Task<DomainServiceResult<ProtectionWriteOutcome>> SetColumnProtectionAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        string datasetId,
        string columnName,
        ProtectionClass? requestedClass,
        string? requestedMaskCategory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(columnName) || columnName.Length > 256 || columnName.Any(char.IsControl))
        {
            return Invalid<ProtectionWriteOutcome>("INVALID_COLUMNS", "Column name is invalid.");
        }

        var meta = await ResolveTenantTableAsync(tenant, datasetId, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return NotFound<ProtectionWriteOutcome>("RESOURCE_NOT_FOUND", "Dataset was not found.");
        }

        var normalizedColumnInput = columnName.Trim();
        var catalogCol = meta.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, normalizedColumnInput, StringComparison.OrdinalIgnoreCase));
        if (catalogCol is null)
        {
            return NotFound<ProtectionWriteOutcome>("COLUMN_NOT_FOUND", "Column was not found on dataset.");
        }

        var canonicalColumn = catalogCol.ColumnName;
        var container = await ResolveContainerForTableAsync(tenant, meta, ct).ConfigureAwait(false);
        var containerRef = container.IsValid ? container.Container : ContainerDefaults.Unassigned.Container;
        if (!await _capabilities.CanClassifyAsync(caller, containerRef, tenant, ct).ConfigureAwait(false))
        {
            return Forbidden<ProtectionWriteOutcome>("Only Data Governance or owner members can update column protection.");
        }

        var (beforeTable, beforeCols) = await _domains.GetProtectionAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false);
        var colNames = meta.Columns.Select(c => c.ColumnName).ToList();
        var beforeEffective = EffectiveProtectionResolver.Resolve(container, beforeTable, beforeCols, colNames);

        var validation = ProtectionWriteValidator.ValidateColumn(beforeEffective.DeclaredTableClass, requestedClass, requestedMaskCategory);
        if (!validation.IsValid || validation.NormalizedProtection is null)
        {
            return Invalid<ProtectionWriteOutcome>(validation.ErrorCode ?? ProtectionWriteValidator.ColumnLooserErrorCode, validation.ErrorMessage ?? "Invalid column protection.");
        }

        await _domains.SetProtectionAsync(tenant, meta.Table.Id, canonicalColumn, validation.NormalizedProtection, caller.UserSid, ct).ConfigureAwait(false);

        var afterCols = new Dictionary<string, ObjectProtection>(beforeCols, StringComparer.OrdinalIgnoreCase)
        {
            [canonicalColumn] = validation.NormalizedProtection
        };
        var afterEffective = EffectiveProtectionResolver.Resolve(container, beforeTable, afterCols, colNames);

        var coveringGrants = afterEffective.TableClass > beforeEffective.TableClass
            ? await CountCoveringTableGrantsAsync(tenant, meta.Identifier, canonicalColumn, ct).ConfigureAwait(false)
            : 0;

        await AuditColumnProtectionChangeAsync(
            tenant,
            caller.UserSid,
            meta,
            container,
            canonicalColumn,
            beforeEffective,
            afterEffective,
            coveringGrants,
            ct).ConfigureAwait(false);

        _cacheInvalidator?.InvalidateTenant(tenant.Value);
        return Ok(new ProtectionWriteOutcome(meta.Identifier.ToString(), meta.Table.Id, canonicalColumn, afterEffective, coveringGrants));
    }

    private async Task<ContainerDefaults> ResolveContainerForTableAsync(TenantId tenant, TableMetadata meta, CancellationToken ct)
    {
        if (_products is not null &&
            string.Equals(meta.Table.SourceName, ProductSource.Name, StringComparison.OrdinalIgnoreCase))
        {
            var product = await _products.GetProductByIdAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false);
            if (product is not null)
            {
                return new ContainerDefaults(
                    new ContainerRef(ContainerKind.DataProduct, product.Id),
                    product.OwnerGroupSid,
                    product.DefaultClass,
                    product.Visibility,
                    IsValid: true);
            }
        }

        return await _domains.GetContainerForTableAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false);
    }

    private async Task<TableMetadata?> ResolveTenantTableAsync(TenantId tenant, string datasetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(datasetId))
        {
            return null;
        }

        var normalized = datasetId.Trim().Replace('/', '.');
        TableMetadata? meta = null;
        if (TableIdentifier.TryParse(normalized, out var tableIdentifier))
        {
            meta = await _tables.GetTableMetadataAsync(tableIdentifier, ct).ConfigureAwait(false);
        }
        else if (Guid.TryParse(datasetId.Trim(), out var tableGuid) && tableGuid != Guid.Empty)
        {
            meta = await CatalogTablePageReader.FindFirstAsync(
                _tables,
                new CatalogTableQuery(IncludeInactive: false),
                t => t.Table.Id == tableGuid,
                ct: ct).ConfigureAwait(false);
        }

        return meta is not null && IsVisibleInTenant(meta, tenant) ? meta : null;
    }

    private static bool IsVisibleInTenant(TableMetadata meta, TenantId tenant) =>
        meta.Table.IsActive &&
        (string.IsNullOrWhiteSpace(meta.OwnerTenantId) ||
         string.Equals(meta.OwnerTenantId, tenant.Value, StringComparison.OrdinalIgnoreCase));

    private async Task<int> CountCoveringTableGrantsAsync(TenantId tenant, TableIdentifier table, string column, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var count = 0;
        Guid? afterId = null;

        for (var pageIdx = 0; pageIdx < 20; pageIdx++)
        {
            var page = await _consents.GetConsentPageAsync(
                new ConsentPageQuery(tenant, Table: table, IncludeInactive: false, AfterId: afterId, PageSize: ConsentPageQuery.MaxPageSize),
                ct).ConfigureAwait(false);
            if (page is null)
            {
                break;
            }

            foreach (var c in page.Items)
            {
                if (c.Effect != ConsentEffect.Allow || !c.IsActive(now))
                {
                    continue;
                }

                var explicitlyDenied = c.ColumnRules.Any(r =>
                    string.Equals(r.ColumnName, column, StringComparison.OrdinalIgnoreCase) &&
                    r.AccessLevel == ColumnAccessLevel.Deny);
                if (!explicitlyDenied)
                {
                    count++;
                }
            }

            if (page.NextAfterId is null || page.Items.Count == 0)
            {
                break;
            }

            afterId = page.NextAfterId;
        }

        return count;
    }

    private async Task AuditTableProtectionChangeAsync(
        TenantId tenant,
        Sid actor,
        TableMetadata meta,
        ContainerDefaults container,
        EffectiveProtection before,
        EffectiveProtection after,
        ProtectionTableChange change,
        CancellationToken ct)
    {
        var wroteClassAudit = false;
        if (before.DeclaredTableClass != after.DeclaredTableClass || before.TableClass != after.TableClass || change.IsLooserThanContainer)
        {
            wroteClassAudit = true;
            await RecordAuditAsync(tenant, actor, AuditEventTypes.ProtectionClassChanged, meta.Identifier.ToString(), null, new
            {
                datasetId = meta.Identifier.ToString(),
                tableId = meta.Table.Id,
                containerId = container.Container.Id,
                containerDefaultClass = (container.DefaultClass ?? ProtectionClass.Confidential).ToCanonicalName(),
                beforeDeclaredClass = before.DeclaredTableClass.ToCanonicalName(),
                afterDeclaredClass = after.DeclaredTableClass.ToCanonicalName(),
                beforeClass = before.TableClass.ToCanonicalName(),
                afterClass = after.TableClass.ToCanonicalName(),
                looserThanContainer = change.IsLooserThanContainer,
                clearedRedundantColumns = change.ClearedRedundantColumns
            }, ct).ConfigureAwait(false);
        }

        if (!string.Equals(before.TableMaskCategory, after.TableMaskCategory, StringComparison.Ordinal))
        {
            await RecordAuditAsync(tenant, actor, AuditEventTypes.MaskCategoryChanged, meta.Identifier.ToString(), null, new
            {
                datasetId = meta.Identifier.ToString(),
                tableId = meta.Table.Id,
                beforeMaskCategory = before.TableMaskCategory,
                afterMaskCategory = after.TableMaskCategory
            }, ct).ConfigureAwait(false);
        }
        else if (!wroteClassAudit)
        {
            await RecordAuditAsync(tenant, actor, AuditEventTypes.ProtectionClassChanged, meta.Identifier.ToString(), null, new
            {
                datasetId = meta.Identifier.ToString(),
                tableId = meta.Table.Id,
                beforeClass = before.TableClass.ToCanonicalName(),
                afterClass = after.TableClass.ToCanonicalName(),
                looserThanContainer = change.IsLooserThanContainer,
                clearedRedundantColumns = change.ClearedRedundantColumns
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task AuditColumnProtectionChangeAsync(
        TenantId tenant,
        Sid actor,
        TableMetadata meta,
        ContainerDefaults container,
        string column,
        EffectiveProtection before,
        EffectiveProtection after,
        int coveringGrantsCount,
        CancellationToken ct)
    {
        before.Columns.TryGetValue(column, out var beforeCol);
        after.Columns.TryGetValue(column, out var afterCol);
        var beforeColClass = beforeCol?.EffectiveClass ?? before.DeclaredTableClass;
        var afterColClass = afterCol?.EffectiveClass ?? after.DeclaredTableClass;
        var beforeColMask = beforeCol?.EffectiveMaskCategory;
        var afterColMask = afterCol?.EffectiveMaskCategory;

        var wroteAny = false;
        if (beforeColClass != afterColClass)
        {
            wroteAny = true;
            await RecordAuditAsync(tenant, actor, AuditEventTypes.ProtectionClassChanged, meta.Identifier.ToString(), column, new
            {
                datasetId = meta.Identifier.ToString(),
                tableId = meta.Table.Id,
                column,
                beforeClass = beforeColClass.ToCanonicalName(),
                afterClass = afterColClass.ToCanonicalName()
            }, ct).ConfigureAwait(false);
        }

        if (after.TableClass > before.TableClass)
        {
            wroteAny = true;
            await RecordAuditAsync(tenant, actor, AuditEventTypes.ProtectionClassChanged, meta.Identifier.ToString(), null, new
            {
                datasetId = meta.Identifier.ToString(),
                tableId = meta.Table.Id,
                reason = "column_rollup",
                triggerColumn = column,
                beforeClass = before.TableClass.ToCanonicalName(),
                afterClass = after.TableClass.ToCanonicalName(),
                coveringGrantsCount,
                ownerGroupSid = container.OwnerGroupSid?.Value,
                ownerGroupNotified = container.OwnerGroupSid is not null
            }, ct).ConfigureAwait(false);
        }

        if (!string.Equals(beforeColMask, afterColMask, StringComparison.Ordinal))
        {
            wroteAny = true;
            await RecordAuditAsync(tenant, actor, AuditEventTypes.MaskCategoryChanged, meta.Identifier.ToString(), column, new
            {
                datasetId = meta.Identifier.ToString(),
                tableId = meta.Table.Id,
                column,
                beforeMaskCategory = beforeColMask,
                afterMaskCategory = afterColMask
            }, ct).ConfigureAwait(false);
        }

        if (!wroteAny)
        {
            await RecordAuditAsync(tenant, actor, AuditEventTypes.ProtectionClassChanged, meta.Identifier.ToString(), column, new
            {
                datasetId = meta.Identifier.ToString(),
                tableId = meta.Table.Id,
                column,
                beforeClass = beforeColClass.ToCanonicalName(),
                afterClass = afterColClass.ToCanonicalName()
            }, ct).ConfigureAwait(false);
        }
    }

    private static IReadOnlyDictionary<string, ObjectProtection> ApplyClearedColumns(
        IReadOnlyDictionary<string, ObjectProtection> existing,
        IReadOnlyList<string> cleared)
    {
        if (cleared.Count == 0)
        {
            return existing;
        }

        var copy = new Dictionary<string, ObjectProtection>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var col in cleared)
        {
            if (copy.TryGetValue(col, out var cur))
            {
                if (string.IsNullOrWhiteSpace(cur.MaskCategory))
                {
                    copy.Remove(col);
                }
                else
                {
                    copy[col] = new ObjectProtection(null, cur.MaskCategory);
                }
            }
        }

        return copy;
    }
}
