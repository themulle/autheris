namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Sql.Internal;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class GovernedSqlExecutor
{
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
            fields["recursiveCte"] = true;   // a rejection before the compile still carries the flag
        }

        if (compiled is not null)
        {
            AddCompiledAuditFields(fields, compiled);
        }
        else if (originalSql is not null)
        {
            // A statement rejected before it compiled has no digest; the hash of its (literal-free) original text identifies it.
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
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(fields)
            // M-1: a DML audit never takes the request token. The statement may already be committed; a client that disconnects must not
            // leave it without its audit record.
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The audit of a DML that is already committed. An audit failure here is never a request failure (the client would retry and
    /// duplicate the write): it is logged as Critical and counted for the operators.
    /// </summary>
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
