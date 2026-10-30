namespace Autheris.Extensions.DataCatalog;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Issue #404 D-5: persists the physical non-unique indexes read by the catalog sync. Replaces the stored list only when it differs
/// (I404-SC-03), writes the minimal audit record (table id and counts only) strictly before the replace, keeps the stored list on any
/// failure and reports codes only, never names or exception text (I404-SC-01, SC-04). A dry run never reaches this type (I404-SC-08).
/// </summary>
internal sealed class PhysicalIndexSync(
    IPhysicalIndexRepository? repository,
    IAuditWriter auditWriter,
    Func<TenantId?> defaultTenant,
    ILogger logger)
{
    internal const string AuditFailedCode = "PHYSICAL_INDEX_AUDIT_FAILED";

    /// <summary>Call after the table exists (its metadata upsert) and before the per-table epoch invalidation (I404-SC-14).</summary>
    public async Task ApplyAsync(
        TableIdentifier table,
        TableMetadata? existing,
        TableMetadata declared,
        PhysicalKeySnapshot physical,
        TenantId? restrictToTenant,
        List<string> warnings,
        CancellationToken ct)
    {
        if (repository is null)
        {
            return;
        }

        var desired = PhysicalNonUniqueIndexResolver.Resolve(declared, physical);
        if (desired is null)
        {
            warnings.Add($"Table '{table}': {PhysicalNonUniqueIndexResolver.UnavailableCode}");
            logger.LogWarning("Table {Table}: {Code}; the stored physical index list is kept.", table, PhysicalNonUniqueIndexResolver.UnavailableCode);
            return;
        }

        var stored = existing?.PhysicalIndexes ?? [];
        if (PhysicalNonUniqueIndexResolver.SameList(stored, desired))
        {
            return;
        }

        if (!await TryAuditAsync(table, existing, restrictToTenant, stored.Count, desired.Count, ct).ConfigureAwait(false))
        {
            warnings.Add($"Table '{table}': {AuditFailedCode}");
            return;
        }

        try
        {
            await repository.ReplacePhysicalIndexesAsync(table, desired, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            warnings.Add($"Table '{table}': {PhysicalNonUniqueIndexResolver.UnavailableCode}");
            logger.LogWarning("Table {Table}: {Code} ({ExceptionType}); the stored physical index list is kept.",
                table, PhysicalNonUniqueIndexResolver.UnavailableCode, ex.GetType().Name);
        }
    }

    private async Task<bool> TryAuditAsync(TableIdentifier table, TableMetadata? existing, TenantId? restrictToTenant, int before, int after, CancellationToken ct)
    {
        try
        {
            var tenant = existing?.OwnerTenantId is { } owner ? new TenantId(owner)
                : restrictToTenant ?? defaultTenant() ?? TenantId.Platform;
            await auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = AuditEventTypes.CatalogPhysicalIndexesChanged,
                ActorSid = new Sid("system:catalog-sync"),
                TargetTable = table.ToString(),
                Decision = "ALLOW",
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { datasetId = table.ToString(), beforeCount = before, afterCount = after })
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                logger.LogWarning("Physical index audit cancelled for table {Table}; the stored list is kept.", table);
            }
            else
            {
                logger.LogWarning("Physical index audit failed for table {Table} ({ExceptionType}); the stored list is kept.", table, ex.GetType().Name);
            }

            return false;
        }
    }
}
