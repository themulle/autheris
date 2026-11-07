namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.ListParameters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class GovernedSqlExecutor
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex AuditListNameRegex();

    private static void AddListAuditField(Dictionary<string, object?> fields, CompiledSql compiled, IReadOnlyDictionary<string, object?>? clientValues)
    {
        SortedDictionary<string, int>? lists = null;
        foreach (var slot in compiled.Parameters)
        {
            if (slot is { Origin: ParameterOrigin.ClientNamed, ListCount: { } count, SourceName: { } name })
            {
                (lists ??= new SortedDictionary<string, int>(StringComparer.Ordinal))[name] = count;
            }
        }

        if (lists is not null)
        {
            fields["list_params"] = lists;
        }

        if (clientValues is not null)
        {
            SortedSet<string>? empty = null;
            foreach (var (name, value) in clientValues)
            {
                if (value is ClientListValue { Count: 0 } && AuditListNameRegex().IsMatch(name))
                {
                    (empty ??= new SortedSet<string>(StringComparer.Ordinal)).Add(name.ToLowerInvariant());
                }
            }

            if (empty is not null)
            {
                fields["list_params_empty"] = empty.ToArray();
            }
        }
    }

    private static void AddCallerAttributeAuditFields(Dictionary<string, object?> fields, GovernedRewrite rewrite, CompiledSql compiled)
    {
        if (rewrite.CallerAttributes is not { Count: > 0 } attributes)
        {
            return;
        }

        fields["caller_attributes"] = attributes.ToDictionary(
            p => p.Key,
            p => p.Value.Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["outcome"] = a.Outcome, ["reason"] = a.Reason }).ToList(),
            StringComparer.OrdinalIgnoreCase);
        fields["caller_attribute_binds"] = compiled.Parameters.Count(p => p.Origin == ParameterOrigin.Policy && p.SourceName != null && Autheris.Application.Identity.Attributes.CallerAttributeParameterName.IsReserved(p.SourceName));
    }

    private void AddCompiledAuditFields(Dictionary<string, object?> fields, CompiledSql compiled, IReadOnlyDictionary<string, object?>? clientValues = null)
    {
        fields["compiler_version"] = compiled.CompilerVersion;
        fields["dialect"] = compiled.Dialect.ToString();
        fields["bind_count"] = compiled.Parameters.Length;
        if (compiled.HasRecursiveCte)
        {
            fields["recursiveCte"] = true;
        }

        AddListAuditField(fields, compiled, clientValues);
        fields["sql.compiled_digest"] = _digester.Compute(compiled);
        if (_options.Value.Audit.StoreCompiledSql)
        {
            fields["compiledSql"] = compiled.Sql;
        }
    }

    private static void AddKeySetAuditFields(Dictionary<string, object?> fields, GovernedRewrite rewrite)
    {
        if (rewrite.KeySets is not { Count: > 0 } outcomes)
        {
            return;
        }

        fields["row_filter_key_sets"] = outcomes.Select(o => new Dictionary<string, object?>
        {
            ["table"] = o.Table,
            ["rule_id"] = o.RuleId,
            ["source"] = o.Source.ToString(),
            ["strategy_source"] = o.StrategySource.ToString(),
            ["outcome"] = o.Outcome.ToString(),
            ["reason"] = o.Reason?.ToString(),
            ["key_count"] = o.KeyCount,
            ["padded_count"] = o.PaddedCount,
            ["age_seconds"] = o.AgeSeconds
        }).ToList();
    }

    private async Task RecordDmlAuditAsync(
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        string eventType,
        string decision,
        CompiledSql? compiled,
        int? affectedRows,
        string? reason,
        bool synthetic,
        string? originalSql = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["statementType"] = dmlContext.StatementType?.ToString(),
            ["tables"] = dmlContext.Tables,
            ["dataSource"] = dsName,
            ["affectedRows"] = affectedRows,
            ["maxAffectedRows"] = _options.Value.WebSql.MaxAffectedRows,
            ["reason"] = reason,
            ["synthetic"] = synthetic
        };
        if (compiled is null && dmlContext.HasRecursiveCte)
        {
            fields["recursiveCte"] = true;
        }

        if (compiled is not null)
        {
            AddCompiledAuditFields(fields, compiled);
        }
        else if (originalSql is not null)
        {
            fields["originalSqlHash"] = _digester.Anonymize(originalSql).OriginalSqlHash;
        }

        await _auditWriter.RecordAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = eventType,
            ActorSid = user.GetUserSid() ?? new Sid("anonymous"),
            TargetTable = string.Join(",", dmlContext.Tables),
            Decision = decision,
            TraceId = TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = JsonSerializer.Serialize(fields)
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RecordCommittedDmlAuditAsync(
        TenantId tenantId, ClaimsPrincipal user, string dsName, DmlAuditContext dmlContext, CompiledSql compiled, int affectedRows)
    {
        try
        {
            await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, AuditEventTypes.WebSqlDmlExecuted, "ALLOW", compiled, affectedRows, reason: null, synthetic: false).ConfigureAwait(false);
        }
        catch (Exception auditEx)
        {
            GatewayDiagnostics.DmlAuditFailedCounter.Add(1);
            _logger?.LogCritical(auditEx,
                "WebSQL DML on data source {DataSource} is committed but its audit record could not be written. TraceId={TraceId}",
                dsName, Activity.Current?.TraceId.ToString());
        }
    }
}
