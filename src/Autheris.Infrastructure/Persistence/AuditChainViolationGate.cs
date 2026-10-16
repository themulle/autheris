using System.Text.Json;
using System.Text.Json.Nodes;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// SR-L-08 / decision G-5: shared admission logic of the three governance repositories once an audit hash chain violation has been
/// latched. The latch itself (<c>_auditChainViolation</c>) is never cleared during the process lifetime.
/// </summary>
internal static class AuditChainViolationGate
{
    internal const string MarkerProperty = "chainViolation";

    internal static AuditChainViolationPolicy ResolvePolicy(AuditOptions? options, bool isDevOrTest) =>
        options?.OnChainViolation ?? (isDevOrTest ? AuditChainViolationPolicy.AlertOnly : AuditChainViolationPolicy.DenyDataAccess);

    /// <summary>Tier-A: security-critical mutations, admin actions and denied requests (synchronous, fail-closed). Everything but allowed query reads.</summary>
    /// <summary>True when at least one entry of the batch is Tier-A (its acknowledgement requires commit and anchor).</summary>
    internal static bool AnyTierA(IReadOnlyList<AuditLogEntry> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        for (int i = 0; i < batch.Count; i++)
        {
            if (IsTierA(batch[i]))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsTierA(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Equals(entry.Decision, "DENY", StringComparison.OrdinalIgnoreCase) ||
               (!string.Equals(entry.EventType, AuditEventTypes.TableQuery, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(entry.EventType, AuditEventTypes.WebSqlQuery, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies the violation policy to an event that is about to be written. Returns the entry to write (a marked copy for Tier-A
    /// events under <see cref="AuditChainViolationPolicy.DenyDataAccess"/>) or throws <see cref="AuditUnavailableException"/> when the event is refused.
    /// </summary>
    internal static AuditLogEntry Admit(
        AuditLogEntry entry,
        bool isTierA,
        string? violation,
        AuditChainViolationPolicy policy,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (violation == null || policy == AuditChainViolationPolicy.AlertOnly)
        {
            return entry;
        }

        if (policy == AuditChainViolationPolicy.DenyAll || !isTierA)
        {
            logger?.LogError(
                "Audit hash chain integrity violation is latched; refusing {Tier} audit event {EventType} (policy {Policy}).",
                isTierA ? "Tier-A" : "Tier-B",
                entry.EventType,
                policy);
            throw new AuditUnavailableException("Audit chain integrity violation (fail-closed).");
        }

        return WithViolationMarker(entry);
    }

    /// <summary>Copy of the entry whose details carry <c>chainViolation=true</c>.</summary>
    internal static AuditLogEntry WithViolationMarker(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AuditLogEntry
        {
            Id = entry.Id,
            TenantId = entry.TenantId,
            OccurredAt = entry.OccurredAt,
            EventType = entry.EventType,
            ActorSid = entry.ActorSid,
            TargetTable = entry.TargetTable,
            TargetColumn = entry.TargetColumn,
            Decision = entry.Decision,
            TraceId = entry.TraceId,
            DetailsJson = AddMarker(entry.DetailsJson)
        };
    }

    /// <summary>The single <c>AUDIT_PIPELINE_FAULT</c> event written per latch.</summary>
    internal static AuditLogEntry CreateFaultEvent(AuditLogEntry trigger, string violation, AuditChainViolationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var details = new JsonObject
        {
            [MarkerProperty] = true,
            ["policy"] = policy.ToString(),
            ["reason"] = violation
        };

        return new AuditLogEntry
        {
            TenantId = trigger.TenantId,
            EventType = AuditEventTypes.AuditPipelineFault,
            ActorSid = trigger.ActorSid,
            TargetTable = "AUDIT_LOG_ENTRIES",
            Decision = "DENY",
            TraceId = trigger.TraceId,
            DetailsJson = details.ToJsonString()
        };
    }

    private static string AddMarker(string? detailsJson)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(detailsJson)
                ? new JsonObject()
                : JsonNode.Parse(detailsJson) as JsonObject ?? new JsonObject { ["original"] = detailsJson };
        }
        catch (JsonException)
        {
            root = new JsonObject { ["original"] = detailsJson };
        }

        root[MarkerProperty] = true;
        return root.ToJsonString();
    }
}
