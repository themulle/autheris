namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class QueryAnalysisService
{
    /// <summary>
    /// Refusals that depend on the tables, columns and policies of the statement. They are reported next to the catalog stage (consent
    /// requests, unknown columns, output columns). Every other refusal concerns the statement itself and ends the item.
    /// </summary>
    private static readonly FrozenSet<string> TableLevelCompileCodes = new[]
    {
        "SQL_POLICY_DENIED",
        GovernedSqlErrorCodes.FilterOnMaskedColumn,
        GovernedSqlErrorCodes.AggregationOnMaskedColumn,
        "REQUIRED_FILTER_COLUMN_MISSING",
        "ROW_POLICY_EXPRESSION_NOT_ALLOWED",
        "TENANT_ID_COLLISION",
        GovernedSqlErrorCodes.EndpointDisabled,
        GovernedSqlErrorCodes.UnknownColumn
    }.ToFrozenSet(StringComparer.Ordinal);

    private async Task<SingleQueryAnalysisOutcome> AnalyzeSqlTextAsync(
        string sql,
        string queryName,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        AnalysisScopeContext scopeContext,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        Dictionary<string, TableMetadata> tableLookup,
        string? compileDataSource,
        bool runCompileParity,
        CancellationToken ct)
    {
        SqlCompileParityFailure? compileFailure = null;
        if (runCompileParity && _compileCheck is not null)
        {
            // The verdict of execution first: one dry run of the governed compile for the effective principal. Anything it cannot
            // classify is thrown (the analysis fails), never reported as valid.
            compileFailure = await _compileCheck.CheckAsync(sql, effectivePrincipal, compileDataSource, ct).ConfigureAwait(false);
            if (compileFailure is not null && !TableLevelCompileCodes.Contains(compileFailure.Code))
            {
                var rejected = new SingleQueryAnalysisOutcome();
                rejected.Diagnostics.Add(ToDiagnostic(compileFailure, queryName));
                return rejected;
            }
        }

        var outcome = await AnalyzeSqlTextCoreAsync(
            sql,
            queryName,
            effectivePrincipal,
            effectiveSid,
            isAdmin,
            scopeContext,
            activeConsents,
            sunsetRules,
            tableLookup,
            adHocStatement: runCompileParity,
            ct).ConfigureAwait(false);

        if (compileFailure is not null)
        {
            var failure = compileFailure;
            if (!outcome.Diagnostics.Exists(d => string.Equals(d.Code, failure.Code, StringComparison.Ordinal)))
            {
                outcome.Diagnostics.Add(ToDiagnostic(failure, queryName));
            }
        }

        return outcome;
    }

    private static QueryValidationDiagnostic ToDiagnostic(SqlCompileParityFailure failure, string queryName) => new(
        failure.Code,
        failure.Message,
        QueryName: queryName,
        Column: string.Equals(failure.SubjectKind, SqlErrorSubject.ColumnName, StringComparison.Ordinal) ? failure.SubjectName : null,
        Location: failure.Line is { } line && failure.Column is { } column ? new QueryDiagnosticLocation(line, column) : null,
        Function: string.Equals(failure.SubjectKind, SqlErrorSubject.Function, StringComparison.Ordinal) ? failure.SubjectName : null,
        Construct: string.Equals(failure.SubjectKind, SqlErrorSubject.Construct, StringComparison.Ordinal) ? failure.SubjectName : null,
        Hint: failure.Hint,
        BlocksExecution: true);

    /// <summary>Statement the legacy parse refused although the compile did not: fixed message, no parser text.</summary>
    private static QueryValidationDiagnostic AdHocSyntaxDiagnostic(string queryName) => new(
        GovernedSqlErrorCodes.SyntaxError,
        GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.SyntaxError),
        QueryName: queryName,
        BlocksExecution: true);

    /// <summary>
    /// A simulated target is evaluated inside the caller's tenant only: a target of another (or an unresolved) tenant is refused
    /// before any compile or catalog read.
    /// </summary>
    private static void EnsureTargetInCallerTenant(ClaimsPrincipal caller, ClaimsPrincipal target)
    {
        var callerTenant = TenantClaimReader.Read(caller);
        var targetTenant = TenantClaimReader.Read(target);
        if (callerTenant.Kind != ClaimTenantKind.Valid ||
            targetTenant.Kind != ClaimTenantKind.Valid ||
            callerTenant.Tenant != targetTenant.Tenant)
        {
            throw new UnauthorizedAccessException("The target principal must belong to the caller's tenant.");
        }
    }
}
