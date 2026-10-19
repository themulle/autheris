namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Failure semantics of one audit write (WP-ARC-B-03). Every table-read call site propagates a failed audit write today
/// (<see cref="Required"/>); the parameter exists so that the audit contract of the audit work package can assign tiers per
/// event without touching the call sites again.
/// </summary>
public enum AuditTier
{
    /// <summary>A failed write fails the request (the current behavior of every call site).</summary>
    Required = 0,

    /// <summary>A failed write is swallowed; the request continues.</summary>
    BestEffort = 1
}

/// <summary>
/// WP-ARC-B-03: the shared pipeline stages of every governed read adapter (REST datasets, GraphQL flat and tree reads,
/// OData, MCP, connectors, procedures): the authorization stage and the audit shape.
/// <para>
/// Authorization: one place evaluates the unified decision point and applies the non-canonical ClusterAdmin fail-closed check
/// (<see cref="EnsureCanonicalClusterAdminClaim"/>), so that no adapter can forget it.
/// </para>
/// <para>
/// Audit: one place builds the <see cref="AuditLogEntry"/> (event name, trace id, JSON details) and writes it through the
/// audit repository (<see cref="WriteAsync"/>, the single seam that a later audit writer replaces).
/// </para>
/// </summary>
public static class GovernedQueryStages
{
    // ---------------------------------------------------------------- authorization

    /// <summary>
    /// WP-X0n / SEC-X1-17: the unified decision point drops a ClusterAdmin role claim of a non-canonical principal while the
    /// governed compiler keeps every role claim. That one input difference fails closed here, before any read.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The principal carries a ClusterAdmin role claim without being a canonical cluster admin.</exception>
    public static void EnsureCanonicalClusterAdminClaim(ClaimsPrincipal user, TableIdentifier table)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.GetUserRoles().Any(ClusterAdminPolicy.IsClusterAdminRole) && !ClusterAdminPolicy.IsCanonicalClusterAdmin(user))
        {
            throw new UnauthorizedAccessException(
                $"Access to table '{table.ToQualifiedName()}' is forbidden: the cluster administrator role claim is not canonical.");
        }
    }

    /// <summary>
    /// Evaluates the unified policy decision point for one table and fails closed: a denied decision and a non-canonical
    /// ClusterAdmin claim both throw <see cref="UnauthorizedAccessException"/>. Returns the allowed decision.
    /// </summary>
    public static async Task<TableAccessDecision> AuthorizeTableAsync(
        IUnifiedPolicyDecisionPoint pdp,
        TableIdentifier table,
        TableMetadata metadata,
        ClaimsPrincipal user,
        TenantId tenantId,
        string? clientIp,
        IReadOnlyList<string>? requestedColumns,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdp);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(user);

        var ip = !string.IsNullOrWhiteSpace(clientIp) && IPAddress.TryParse(clientIp, out var parsed) ? parsed : null;
        var securityContext = SecurityPrincipalContext.FromPrincipal(user, tenantId, clientIp: ip);

        var decision = await pdp.EvaluateAccessAsync(table, metadata, securityContext, requestedColumns, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by governance policy.";
            throw new UnauthorizedAccessException($"Access to table '{table.ToQualifiedName()}' is forbidden: {reasons}");
        }

        EnsureCanonicalClusterAdminClaim(user, table);
        return decision;
    }

    // ---------------------------------------------------------------- audit shape

    /// <summary>
    /// Details of a table access decision: <c>is_allowed</c>, <c>reasons</c>, optional <c>path</c> and
    /// <c>virtual_filters</c> (the field names of the table-query audit entries).
    /// </summary>
    public static Dictionary<string, object?> DecisionDetails(TableAccessDecision decision, string? path = null, bool includeVirtualFilters = true)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_allowed"] = decision.IsAllowed,
            ["reasons"] = decision.DeniedReasons
        };
        if (path != null)
        {
            details["path"] = path;
        }

        if (includeVirtualFilters)
        {
            details["virtual_filters"] = decision.AppliedVirtualFilters;
        }

        return details;
    }

    /// <summary>Adds the pushed-down query shape (<c>filter</c>, <c>order_by</c>) to table-query details.</summary>
    public static Dictionary<string, object?> WithQueryShape(
        Dictionary<string, object?> details,
        TableFilterClause? filter,
        IReadOnlyList<TableOrderBy>? orderBy)
    {
        ArgumentNullException.ThrowIfNull(details);
        details["filter"] = filter?.SqlPredicate;
        details["order_by"] = orderBy?.Select(o => $"{o.Column} {(o.Descending ? "DESC" : "ASC")}").ToArray();
        return details;
    }

    /// <summary>The one shape of an audit entry of a governed read (trace id defaults to the current W3C trace).</summary>
    public static AuditLogEntry BuildEntry(
        string eventType,
        TenantId tenant,
        Sid actor,
        string target,
        bool allowed,
        object? details,
        string? traceId = null) =>
        new()
        {
            TenantId = tenant,
            EventType = eventType,
            ActorSid = actor,
            TargetTable = target,
            Decision = allowed ? "ALLOW" : "DENY",
            TraceId = traceId ?? TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = JsonSerializer.Serialize(details ?? new { })
        };

    /// <summary>
    /// The single write seam of the read stages. <see cref="AuditTier.Required"/> propagates a failed write (the behavior of
    /// every call site before this stage existed).
    /// </summary>
    public static async Task WriteAsync(IAuditLogRepository audit, AuditLogEntry entry, AuditTier tier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(entry);

        if (tier == AuditTier.Required)
        {
            await audit.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await audit.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort by contract: the request must not fail because of this entry.
        }
    }

    /// <summary>
    /// The write seam over the unified audit contract (WP-ARC-A-06): <see cref="AuditTier.Required"/> maps to
    /// <see cref="IAuditWriter.RecordRequiredAsync"/>, <see cref="AuditTier.BestEffort"/> to <see cref="IAuditWriter.RecordBestEffortAsync"/>.
    /// </summary>
    public static Task WriteAsync(IAuditWriter audit, AuditLogEntry entry, AuditTier tier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(entry);

        return tier == AuditTier.Required
            ? audit.RecordRequiredAsync(entry, ct)
            : audit.RecordBestEffortAsync(entry, ct);
    }

    /// <summary>Builds and writes a <c>TABLE_QUERY</c>-family entry.</summary>
    public static Task RecordTableReadAsync(
        IAuditLogRepository audit,
        TenantId tenant,
        Sid actor,
        TableIdentifier table,
        bool allowed,
        object? details,
        CancellationToken ct,
        AuditTier tier = AuditTier.Required,
        string eventType = AuditEventTypes.TableQuery,
        string? traceId = null,
        IConsumerUsageRecorder? usageRecorder = null)
    {
        usageRecorder?.RecordUsage(
            table,
            tenant,
            actor,
            allowed ? ConsumerOutcome.Allow : ConsumerOutcome.DenyAccess,
            rowsReturned: 0,
            rowsMeasured: false);

        return WriteAsync(audit, BuildEntry(eventType, tenant, actor, table.ToString(), allowed, details, traceId), tier, ct);
    }

    /// <summary>Builds and writes a <c>TABLE_QUERY</c>-family entry through the unified audit contract.</summary>
    public static Task RecordTableReadAsync(
        IAuditWriter audit,
        TenantId tenant,
        Sid actor,
        TableIdentifier table,
        bool allowed,
        object? details,
        CancellationToken ct,
        AuditTier tier = AuditTier.Required,
        string eventType = AuditEventTypes.TableQuery,
        string? traceId = null,
        IConsumerUsageRecorder? usageRecorder = null)
    {
        usageRecorder?.RecordUsage(
            table,
            tenant,
            actor,
            allowed ? ConsumerOutcome.Allow : ConsumerOutcome.DenyAccess,
            rowsReturned: 0,
            rowsMeasured: false);

        return WriteAsync(audit, BuildEntry(eventType, tenant, actor, table.ToString(), allowed, details, traceId), tier, ct);
    }
}
