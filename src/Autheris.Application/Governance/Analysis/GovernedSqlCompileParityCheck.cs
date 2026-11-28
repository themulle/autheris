namespace Autheris.Application.Governance.Analysis;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// <see cref="ISqlCompileParityCheck"/> over <see cref="IGovernedSqlExecutionService.ValidateAsync(string, ClaimsPrincipal, TenantId, string?, CancellationToken)"/>.
/// The error is mapped with the mapper every execution surface uses, so code, fixed message, position, subject and hint are the ones execution answers.
/// </summary>
public sealed class GovernedSqlCompileParityCheck(IGovernedSqlExecutionService execution) : ISqlCompileParityCheck
{
    public async Task<SqlCompileParityFailure?> CheckAsync(string sql, ClaimsPrincipal principal, string? dataSourceName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);

        try
        {
            await execution.ValidateAsync(sql, principal, principal.GetTenantId(), dataSourceName, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (TryDescribe(ex, out var failure))
        {
            return failure;
        }
    }

    private static bool TryDescribe(Exception ex, out SqlCompileParityFailure? failure)
    {
        failure = null;
        var classified = SqlFailureClassifier.Classify(ex);
        if (classified.Kind is not (SqlFailureKind.InvalidQuery or SqlFailureKind.PolicyDenied or SqlFailureKind.NotSupported))
        {
            return false;   // throttling, timeouts, cancellation and server errors are the analysis' own failure, never "valid"
        }

        if (SqlCompilerErrorMapper.TryMap(ex, out var mapped))
        {
            // A policy denial carries no name, position or hint: hidden objects stay indistinguishable from missing ones.
            failure = string.Equals(mapped.Code, GovernedSqlErrorCodes.PolicyDenied, StringComparison.Ordinal)
                ? new SqlCompileParityFailure(mapped.Code, mapped.Title, null, null, null, null, null)
                : new SqlCompileParityFailure(mapped.Code, mapped.Title, mapped.Line, mapped.Column, mapped.Subject?.Kind, mapped.Subject?.Name, mapped.Hint);
            return true;
        }

        var code = classified.Code ?? classified.Kind switch
        {
            SqlFailureKind.PolicyDenied => "ACCESS_DENIED",
            SqlFailureKind.NotSupported => "NOT_IMPLEMENTED",
            _ => "BAD_REQUEST"
        };
        failure = new SqlCompileParityFailure(code, classified.FixedMessage, classified.Line, classified.Column, null, null, null);
        return true;
    }
}
