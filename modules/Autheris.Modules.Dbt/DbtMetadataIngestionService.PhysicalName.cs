namespace Autheris.Modules.Dbt;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class DbtMetadataIngestionService
{
    private static bool IsAliased(DbtModelDefinition model) =>
        !string.Equals(model.PhysicalName, model.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Issue #666: an entry is updated by the node whose unique id it records; an entry without a recorded id only by a node
    /// without alias or identifier (the identity the sync used before), which then records its id. Anything else is another origin.
    /// </summary>
    private static bool OwnsEntry(Table entry, DbtModelDefinition model) =>
        entry.DbtUniqueId is { } owner
            ? string.Equals(owner, model.UniqueId, StringComparison.Ordinal)
            : !IsAliased(model);

    private void RejectCollision(CatalogCreationRun run, DbtModelDefinition model, List<string> warnings)
    {
        var id = DbtIdentifierSanitizer.ForLog(model.UniqueId);
        var kind = string.Equals(model.Materialization, "source", StringComparison.OrdinalIgnoreCase) ? "source" : "model";
        run.RejectedIds.Add(model.UniqueId);
        run.SkippedTotal++;
        if (run.Skipped.Count < MaxSkippedItemsReported)
        {
            run.Skipped.Add(new DbtSyncSkippedItem(id, kind, DbtSyncSkipReasons.PhysicalNameCollision));
        }

        warnings.Add($"{DbtSyncSkipReasons.PhysicalNameCollision}: dbt {kind} '{id}' was not imported, its physical table name is already taken.");
        _logger.LogWarning("dbt sync skipped {Kind} '{Name}': {Reason}", kind, id, DbtSyncSkipReasons.PhysicalNameCollision);
    }

    /// <summary>Issue #666: an entry registered under the logical name stays as it is; the sync now registers the physical name.</summary>
    private async Task WarnIfPhysicalNameChangedAsync(DbtModelDefinition model, List<string> warnings, CancellationToken ct)
    {
        if (!IsAliased(model) || !TableIdentifier.TryCreate(model.Database, model.Schema, model.Name, out var logical))
        {
            return;
        }

        TableMetadata? old;
        try
        {
            old = await _metadataRepository.GetTableMetadataAsync(logical, ct).ConfigureAwait(false);
        }
        catch (TableMetadataUnavailableException)
        {
            return;
        }

        if (old != null && (old.Table.DbtUniqueId is null || string.Equals(old.Table.DbtUniqueId, model.UniqueId, StringComparison.Ordinal)))
        {
            warnings.Add($"PHYSICAL_NAME_CHANGED: dbt node '{DbtIdentifierSanitizer.ForLog(model.Name)}' now registers '{DbtIdentifierSanitizer.ForLog(model.PhysicalName)}'; the entry for the logical name is left unchanged.");
        }
    }

    private static bool IsRoutine(string tableName, JsonElement item)
    {
        if (tableName.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
            tableName.StartsWith("fn_", StringComparison.OrdinalIgnoreCase) ||
            tableName.StartsWith("usp_", StringComparison.OrdinalIgnoreCase) ||
            tableName.StartsWith("ufn_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.TryGetProperty("type", out var typeProp) &&
            typeProp.GetString() is string typeStr &&
            (string.Equals(typeStr, "routine", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(typeStr, "procedure", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(typeStr, "function", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (item.TryGetProperty("materialization", out var matProp) &&
            matProp.GetString() is string matStr &&
            (string.Equals(matStr, "routine", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(matStr, "procedure", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(matStr, "function", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static TableIdentifier ParseTableIdentifier(string raw, string? explicitSchema = null, string? explicitDatabase = null)
    {
        var parts = raw.Split('.');
        if (parts.Length == 1)
        {
            var db = !string.IsNullOrWhiteSpace(explicitDatabase) ? explicitDatabase : "default";
            var sc = !string.IsNullOrWhiteSpace(explicitSchema) ? explicitSchema : "public";
            return new TableIdentifier(db, sc, parts[0]);
        }
        if (parts.Length == 2)
        {
            var db = !string.IsNullOrWhiteSpace(explicitDatabase) ? explicitDatabase : "default";
            return new TableIdentifier(db, parts[0], parts[1]);
        }
        return new TableIdentifier(parts[0], parts[1], parts[2]);
    }
}
