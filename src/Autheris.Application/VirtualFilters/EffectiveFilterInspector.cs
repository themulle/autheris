namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>Default <see cref="IEffectiveFilterInspector"/>. Audit failures propagate (fail closed).</summary>
public sealed class EffectiveFilterInspector(
    MandatoryRowFilterResolver resolver,
    ITableMetadataRepository catalog,
    IAuditWriter audit) : IEffectiveFilterInspector
{
    public async Task<EffectiveFilterInspection> InspectAsync(SecurityPrincipalContext caller, EffectiveFilterQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(query);

        if (!caller.HasRole(GatewayRole.FilterAdmin) && !caller.HasRole(GatewayRole.GovernanceAdmin) && !caller.HasRole(GatewayRole.SecurityAuditor))
        {
            await audit.RecordRequiredAsync(new AuditLogEntry
            {
                EventType = "VIRTUAL_FILTER_ADMIN_DENIED",
                TargetTable = "effective-filters",
                ActorSid = caller.UserSid,
                TenantId = caller.TenantId,
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new { error = "Forbidden: missing required role for effective-filters" }),
                OccurredAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
            return new EffectiveFilterInspection { Kind = EffectiveFilterInspectionKind.Forbidden };
        }

        string? user = query.User;
        if (string.IsNullOrWhiteSpace(user))
        {
            return BadRequest("The query parameter 'user' (SID) is required.");
        }

        var tenant = ResolveTenant(caller, query.Tenant);
        if (tenant == null)
        {
            return new EffectiveFilterInspection { Kind = EffectiveFilterInspectionKind.Forbidden };
        }

        static HashSet<T> List<T>(string? value, Func<string, T> create) =>
            (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(create).ToHashSet();
        var groups = List<Sid>(query.Groups, v => new Sid(v));
        var roles = List<string>(query.Roles, v => v);
        MandatoryFilterQuery For(TableMetadata table) => new(new Sid(user), groups, roles, tenant.Value, table);

        string? tableName = query.Table;
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            if (!TableIdentifier.TryParse(tableName, out var id))
            {
                return BadRequest("The query parameter 'table' must be domain.schema.table.");
            }

            var table = await catalog.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
            if (table == null)
            {
                return new EffectiveFilterInspection
                {
                    Kind = EffectiveFilterInspectionKind.NotFound,
                    Error = $"The table '{tableName}' is not in the catalog."
                };
            }

            var explanation = await resolver.ExplainAsync(For(table), ct).ConfigureAwait(false);

            await audit.RecordRequiredAsync(new AuditLogEntry
            {
                EventType = "VIRTUAL_FILTER_EFFECTIVE_INSPECTED",
                TargetTable = table.Identifier.ToString(),
                ActorSid = caller.UserSid,
                TenantId = tenant.Value,
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new { user, table = table.Identifier.ToString() }),
                OccurredAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);

            return new EffectiveFilterInspection
            {
                Kind = EffectiveFilterInspectionKind.SingleTable,
                User = user,
                Table = table,
                Explanation = explanation
            };
        }

        int page = int.TryParse(query.Page, out var p) && p > 0 ? p : 1;
        int pageSize = int.TryParse(query.PageSize, out var ps) ? Math.Clamp(ps, 1, 500) : 100;
        var tables = (await catalog.GetAllTablesAsync(ct).ConfigureAwait(false))
            .OrderBy(t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = new List<EffectiveFilterListItem>();
        foreach (var table in tables.Skip((page - 1) * pageSize).Take(pageSize))
        {
            var explanation = await resolver.ExplainAsync(For(table), ct).ConfigureAwait(false);
            items.Add(new EffectiveFilterListItem(
                table.Identifier.ToString(),
                explanation.Decision,
                explanation.Outcome.AppliedFilters,
                explanation.Outcome.DenyReason));
        }

        await audit.RecordRequiredAsync(new AuditLogEntry
        {
            EventType = "VIRTUAL_FILTER_EFFECTIVE_INSPECTED",
            TargetTable = "all_tables",
            ActorSid = caller.UserSid,
            TenantId = tenant.Value,
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new { user, count = items.Count }),
            OccurredAt = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);

        return new EffectiveFilterInspection
        {
            Kind = EffectiveFilterInspectionKind.Page,
            User = user,
            Items = items,
            PageNo = page,
            PageSize = pageSize,
            Total = tables.Count
        };
    }

    private static EffectiveFilterInspection BadRequest(string error) =>
        new() { Kind = EffectiveFilterInspectionKind.BadRequest, Error = error };

    /// <summary>The caller's tenant, or the requested one for ClusterAdmin (decision 3); null when forbidden.</summary>
    private static TenantId? ResolveTenant(SecurityPrincipalContext security, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, security.TenantId.Value, StringComparison.Ordinal))
        {
            return security.TenantId;
        }

        return security.IsClusterAdmin ? new TenantId(requested) : (TenantId?)null;
    }
}
