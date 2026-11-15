namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Policy;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed partial class GovernedProcedureExecutionService
{
    private void RecordUnknownProcedureUsage(string? name, ClaimsPrincipal user, TenantId tenantId)
    {
        if (_usageRecorder == null)
        {
            return;
        }

        var sid = user.GetUserSid();
        if (sid == null)
        {
            return;
        }

        string? connName = _options.Value.SqlEndpoints.Procedures.ConnectionName;
        string dataSource = connName ?? string.Empty;
        string target = name != null && TargetNameRegex.IsMatch(name) ? name : InvalidTargetMarker;
        var tableId = ResolveProcedureTableId(dataSource, target);
        _usageRecorder.Record(
            new UsageFact(tableId, dataSource, UsageOutcome.DenyAccess, 0, false, 0, Array.Empty<GrantRef>(), default),
            tenantId,
            sid.Value);
    }

    private static IReadOnlyList<GrantRef> ExtractDecisionGrants(IEnumerable<TableAccessDecision> decisions)
    {
        List<GrantRef>? grants = null;
        HashSet<string>? seen = null;
        foreach (var decision in decisions)
        {
            if (!decision.IsAllowed || decision.Grants.Count == 0)
            {
                continue;
            }

            foreach (var g in GovernedQueryStages.ToGrantRefs(decision))
            {
                seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (seen.Add(g.GrantId))
                {
                    grants ??= [];
                    grants.Add(g);
                }
            }
        }

        return grants ?? (IReadOnlyList<GrantRef>)Array.Empty<GrantRef>();
    }

    private void RecordProcedureUsage(
        ProcedureDefinition definition,
        TenantId tenantId,
        Sid userSid,
        UsageOutcome outcome,
        long rows,
        bool rowsMeasured,
        IReadOnlyList<GrantRef>? grants = null)
    {
        if (_usageRecorder == null)
        {
            return;
        }

        string dataSource = string.IsNullOrWhiteSpace(definition.DataSource)
            ? (_options.Value.SqlEndpoints.Procedures.ConnectionName ?? string.Empty)
            : definition.DataSource;
        var tableId = ResolveProcedureTableId(dataSource, definition.ProcedureName);
        _usageRecorder.Record(
            new UsageFact(tableId, dataSource, outcome, rows, rowsMeasured, 0, grants ?? Array.Empty<GrantRef>(), default),
            tenantId,
            userSid);
    }

    private static TableIdentifier ResolveProcedureTableId(string dataSource, string procedureName)
    {
        string domain = string.IsNullOrWhiteSpace(dataSource) ? "procedures" : dataSource.Replace('.', '_');
        string schema = "procedures";
        string table = InvalidTargetMarker;

        if (!string.IsNullOrWhiteSpace(procedureName))
        {
            var parts = procedureName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                schema = parts[0];
                table = parts[1];
            }
            else if (parts.Length >= 3)
            {
                schema = parts[^2];
                table = parts[^1];
            }
            else if (parts.Length == 1)
            {
                table = parts[0];
            }
        }

        return TableIdentifier.TryCreate(domain, schema, table, out var tableId)
            ? tableId
            : new TableIdentifier("procedures", "procedures", InvalidTargetMarker);
    }
}
