namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed partial class TableAccessPolicy
{
    /// <summary>
    /// SEC C-03: applies a Casbin decision as an additional restriction on the consent decision. Column levels can only be
    /// lowered (an explicit Clear can only come from consent), row filters are combined with AND, and row filter parameters
    /// are merged; two filters binding the same name to different values deny (ambiguous, fail-closed).
    /// </summary>
    public static TableAccessDecision Restrict(TableAccessDecision consentDecision, TableAccessDecision policyDecision, TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(consentDecision);
        ArgumentNullException.ThrowIfNull(policyDecision);
        ArgumentNullException.ThrowIfNull(metadata);

        var mergedColumns = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in metadata.Columns)
        {
            var policyLevel = policyDecision.GetColumnAccess(col.ColumnName);
            if (consentDecision.ColumnAccess.TryGetValue(col.ColumnName, out var explicitLevel))
            {
                mergedColumns[col.ColumnName] = explicitLevel < policyLevel ? explicitLevel : policyLevel;
            }
            else if (consentDecision.HasUnconstrainedColumnAllow && policyLevel != ColumnAccessLevel.Clear)
            {
                mergedColumns[col.ColumnName] = policyLevel;
            }
        }

        // WP-X0e: a typed Casbin policy is AND-merged as a typed policy (one projection, one marker namespace). Parameter names bound
        // to different values deny, exactly like the text path below.
        if (policyDecision.RowPolicy is { } typedPolicy && metadata.DataSourceType == DataSourceType.Sql)
        {
            TableAccessDecision typed;
            try
            {
                typed = consentDecision.WithAdditionalRowPolicy(typedPolicy);
            }
            catch (System.Security.SecurityException)
            {
                return TableAccessDecision.Denied(consentDecision.Table, "Conflicting row-level security parameters.");
            }

            var projected = typedPolicy.Project().Parameters;
            var others = policyDecision.RowFilterParameters?.Where(p => !projected.ContainsKey(p.Key)).ToList();
            var typedParameters = typed.RowFilterParameters;
            if (others is { Count: > 0 })
            {
                if (!TryMergeParameters(out var mergedOthers, typedParameters, others.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)))
                {
                    return TableAccessDecision.Denied(consentDecision.Table, "Conflicting row-level security parameters.");
                }

                typedParameters = mergedOthers;
            }

            return typed with { ColumnAccess = mergedColumns, RowFilterParameters = typedParameters };
        }

        var mergedParameters = consentDecision.RowFilterParameters;
        if (policyDecision.RowFilterParameters is { Count: > 0 })
        {
            if (!TryMergeParameters(out var merged, consentDecision.RowFilterParameters, policyDecision.RowFilterParameters))
            {
                return TableAccessDecision.Denied(consentDecision.Table, "Conflicting row-level security parameters.");
            }

            mergedParameters = merged;
        }

        // F-GOV-14 WI-3: the ABAC filter is added through the decision helper so it survives the removal of an enforced
        // Virtual Filter predicate (WithoutEnforcedVirtualFilters).
        return consentDecision.WithAdditionalRowFilter(policyDecision.CombinedRowFilterSql ?? string.Empty) with
        {
            ColumnAccess = mergedColumns,
            RowFilterParameters = mergedParameters
        };
    }

    private static bool TryMergeParameters(
        out IReadOnlyDictionary<string, object?>? merged,
        params IReadOnlyDictionary<string, object?>?[] sources)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var (name, value) in source ?? new Dictionary<string, object?>())
            {
                if (result.TryGetValue(name, out var existing) && !Equals(existing, value))
                {
                    merged = null;
                    return false;
                }

                result[name] = value;
            }
        }

        merged = result;
        return true;
    }

    private SecurityEvaluationContext BuildEvaluationContext(TableAccessQuery query)
    {
        var claims = query.Claims?.ToList() ?? [];
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in claims)
        {
            // SR15-08: Action claims from client tokens MUST NOT spoof gateway-internal action resolution!
            if (string.Equals(claim.Type, "action", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "gql.action", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            attributes[claim.Type] = claim.Value;
        }

        var purpose = claims.FirstOrDefault(c => c.Type == "purpose")?.Value ?? claims.FirstOrDefault(c => c.Type == "purpose_id")?.Value;

        foreach (var (key, value) in query.ExtraAttributes ?? new Dictionary<string, object?>())
        {
            attributes[key] = value;
        }

        return new SecurityEvaluationContext(
            UserSid: query.UserSid,
            GroupSids: query.GroupSids,
            Tenant: query.Tenant,
            TargetTable: query.Metadata.Identifier,
            RequestedColumns: query.RequestedColumns ?? query.Metadata.Columns.Select(c => c.ColumnName).ToList(),
            // SEC H-4 / RV-01: an unknown client IP never satisfies network rules (fail-closed); never from token claims.
            ClientIp: query.ClientIp ?? _clientIpResolver?.ResolveClientIp() ?? IPAddress.None,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: purpose,
            Attributes: attributes,
            TargetDialect: query.Metadata.Dialect)
        {
            TableColumns = query.Metadata.Columns.Select(c => c.ColumnName).ToList()
        };
    }
}
