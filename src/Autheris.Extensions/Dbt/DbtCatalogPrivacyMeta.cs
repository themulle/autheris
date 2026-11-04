namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Issue #448 (WP-4, SEC-MGS-05, SEC-MGS-08, SEC-MGS-09):
/// Ingestion of catalog privacy metadata (autheris.min_group_size and autheris.non_personal) from dbt manifests.
/// </summary>
internal static class DbtCatalogPrivacyMeta
{
    public static async Task<(int? MinGroupSize, bool NonPersonal)> ApplyAsync(
        DbtModelDefinition model,
        TableIdentifier tableId,
        int? storedMinGroupSize,
        bool storedNonPersonal,
        IAuditWriter? auditWriter,
        TenantId tenantId,
        List<string> warnings,
        ILogger logger,
        CancellationToken ct)
    {
        // 1. Extract raw keys (flat or nested)
        var (rawMinGroup, rawNonPersonal, hasMinGroupKey, hasNonPersonalKey) = ExtractRaw(model.Meta);

        // 2. Parse min_group_size: present = value, absent = cleared (null)
        int? targetMinGroupSize = null;
        if (hasMinGroupKey)
        {
            if (rawMinGroup is null)
            {
                targetMinGroupSize = null;
            }
            else if (!CatalogMinGroupSizeRules.TryParseMinGroupSize(rawMinGroup, out targetMinGroupSize, out var error, allowNone: false))
            {
                warnings.Add($"Table '{tableId}': invalid autheris.min_group_size '{rawMinGroup}': {error}; clearing value.");
                targetMinGroupSize = null;
            }
        }

        // 3. Parse non_personal: present = value, absent = cleared (false)
        bool targetNonPersonal = false;
        if (hasNonPersonalKey)
        {
            if (rawNonPersonal is null)
            {
                targetNonPersonal = false;
            }
            else if (!CatalogMinGroupSizeRules.TryParseNonPersonal(rawNonPersonal, out var parsedNp) || parsedNp is null)
            {
                warnings.Add($"Table '{tableId}': invalid autheris.non_personal '{rawNonPersonal}'; clearing to false.");
                targetNonPersonal = false;
            }
            else
            {
                targetNonPersonal = parsedNp.Value;
            }
        }

        // 4. Column personal data / PII gate: if any column has pii=true or email heuristic, force non_personal = false
        if (targetNonPersonal && HasPiiColumn(model, out var piiCol, out var piiReason))
        {
            targetNonPersonal = false;
            warnings.Add($"Table '{tableId}': column '{piiCol}' contains personal data ({piiReason}); autheris.non_personal forced to false.");
        }

        // 5. Change detection
        bool changed = storedMinGroupSize != targetMinGroupSize || storedNonPersonal != targetNonPersonal;
        if (!changed)
        {
            return (storedMinGroupSize, storedNonPersonal);
        }

        // 6. Fail-closed audit check
        if (auditWriter is null)
        {
            warnings.Add($"Table '{tableId}': minimum group size or non-personal change could not be audited because no audit writer is configured; keeping stored values.");
            return (storedMinGroupSize, storedNonPersonal);
        }

        // 7. Audit CATALOG_MIN_GROUP_SIZE_CHANGED
        await auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = AuditEventTypes.CatalogMinGroupSizeChanged,
            ActorSid = new Sid("system:dbt-sync"),
            TargetTable = tableId.ToString(),
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                before = new { minGroupSize = storedMinGroupSize, nonPersonal = storedNonPersonal },
                after = new { minGroupSize = targetMinGroupSize, nonPersonal = targetNonPersonal },
                source = "dbt"
            })
        }, ct).ConfigureAwait(false);

        return (targetMinGroupSize, targetNonPersonal);
    }

    private static (string? RawMinGroupSize, string? RawNonPersonal, bool HasMinGroupKey, bool HasNonPersonalKey) ExtractRaw(IReadOnlyDictionary<string, string> meta)
    {
        string? rawMinGroup = null;
        string? rawNonPersonal = null;
        bool hasMinGroup = false;
        bool hasNonPersonal = false;

        if (meta.TryGetValue("autheris.min_group_size", out var flatMgs))
        {
            rawMinGroup = flatMgs;
            hasMinGroup = true;
        }

        if (meta.TryGetValue("autheris.non_personal", out var flatNp))
        {
            rawNonPersonal = flatNp;
            hasNonPersonal = true;
        }

        if (meta.TryGetValue("autheris", out var autherisJson) && !string.IsNullOrWhiteSpace(autherisJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(autherisJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (!hasMinGroup)
                    {
                        if (doc.RootElement.TryGetProperty("min_group_size", out var mgsProp))
                        {
                            rawMinGroup = JsonValueText.From(mgsProp);
                            hasMinGroup = true;
                        }
                        else if (doc.RootElement.TryGetProperty("minGroupSize", out var mgsCamelProp))
                        {
                            rawMinGroup = JsonValueText.From(mgsCamelProp);
                            hasMinGroup = true;
                        }
                    }

                    if (!hasNonPersonal)
                    {
                        if (doc.RootElement.TryGetProperty("non_personal", out var npProp))
                        {
                            rawNonPersonal = JsonValueText.From(npProp);
                            hasNonPersonal = true;
                        }
                        else if (doc.RootElement.TryGetProperty("nonPersonal", out var npCamelProp))
                        {
                            rawNonPersonal = JsonValueText.From(npCamelProp);
                            hasNonPersonal = true;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // malformed json
            }
        }

        return (rawMinGroup, rawNonPersonal, hasMinGroup, hasNonPersonal);
    }

    private static bool HasPiiColumn(DbtModelDefinition model, out string? columnName, out string? reason)
    {
        foreach (var (colName, colDef) in model.Columns)
        {
            if (colDef.Meta.TryGetValue("pii", out var piiVal) &&
                (string.Equals(piiVal, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(piiVal, "1", StringComparison.OrdinalIgnoreCase)))
            {
                columnName = colName;
                reason = "meta.pii=true";
                return true;
            }

            if (colDef.Tags.Any(t => t.Contains("email", StringComparison.OrdinalIgnoreCase)) ||
                colName.Contains("email", StringComparison.OrdinalIgnoreCase))
            {
                columnName = colName;
                reason = "email";
                return true;
            }
        }

        columnName = null;
        reason = null;
        return false;
    }
}
