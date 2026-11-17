namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Internal;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class GovernedSqlExecutor
{
    private readonly IWriteBudgetStore? _writeBudgetStore;

    /// <summary>
    /// Enforces dedicated write-connection isolation: DML always runs on <c>WriteConnectionString</c> (or fails closed),
    /// while non-DML queries always use the standard read <c>ConnectionString</c>.
    /// </summary>
    private static DataSourceConnectionOptions ResolveEffectiveConnectionOptions(
        DataSourceConnectionOptions connOptions,
        bool isDml)
    {
        if (!isDml)
        {
            return connOptions;
        }

        if (string.IsNullOrWhiteSpace(connOptions.WriteConnectionString))
        {
            throw new WebSqlPolicyException(
                "Data source has no dedicated write connection configured (dml_write_connection_missing).",
                GovernedSqlErrorCodes.DmlWriteConnectionMissing);
        }

        return connOptions.ForWriteExecution(connOptions.WriteConnectionString);
    }

    private async Task<long> PrepareDmlExecutionLimitsAsync(
        TenantId tenantId,
        ClaimsPrincipal user,
        GovernedRewrite rewrite,
        CancellationToken ct)
    {
        var webSql = _options.Value.WebSql;
        var limit = new WriteBudgetLimit(
            webSql.WriteStatementsPerMinute,
            webSql.WriteRowsPerHour,
            rewrite.WriteMaxRowsPerStatement,
            webSql.MaxAffectedRows);

        if (_writeBudgetStore is null)
        {
            return limit.EffectiveStatementMaxRows();
        }

        string subjectSid = ResolveCallerSid(user);
        WriteBudgetAcquisition acq = await _writeBudgetStore
            .TryAcquireStatementAsync(tenantId, subjectSid, limit, ct)
            .ConfigureAwait(false);

        if (!acq.Allowed)
        {
            throw new WriteBudgetExhaustedException(
                acq.DenyReason ?? "write_budget_exhausted",
                acq.RetryAfterSeconds);
        }

        return acq.EffectiveMaxRowsPerStatement;
    }

    private async Task OnBeforeDmlCommitAsync(
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        CompiledSql compiled,
        IReadOnlyDictionary<string, object?>? clientValues,
        int affectedRows,
        CancellationToken ct)
    {
        if (_writeBudgetStore is not null)
        {
            string subjectSid = ResolveCallerSid(user);
            long rowsPerHour = _options.Value.WebSql.WriteRowsPerHour;
            bool withinBudget = await _writeBudgetStore
                .TryRecordAffectedRowsAsync(tenantId, subjectSid, affectedRows, rowsPerHour, ct)
                .ConfigureAwait(false);
            if (!withinBudget)
            {
                throw new WriteBudgetExhaustedException("write_budget_exhausted");
            }
        }

        // Write-ahead / pre-commit outcome audit: any audit store failure throws before transaction.CommitAsync, rolling back the DML.
        await RecordDmlAuditAsync(
            tenantId,
            user,
            dsName,
            dmlContext,
            AuditEventTypes.WebSqlDmlExecuted,
            "ALLOW",
            compiled,
            affectedRows,
            reason: null,
            synthetic: false,
            clientValues: clientValues).ConfigureAwait(false);
    }

    private static string ResolveCallerSid(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.PrimarySid)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? "anonymous";
}
