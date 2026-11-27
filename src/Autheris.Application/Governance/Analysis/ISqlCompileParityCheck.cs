namespace Autheris.Application.Governance.Analysis;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Dry run of the governed SQL compile for query analysis: the verdict execution would give an ad-hoc statement for the same principal
/// and data source, without a connection, cost admission, quota charge or deny audit.
/// </summary>
public interface ISqlCompileParityCheck
{
    /// <summary>Null when execution would accept the statement; the fixed failure it would answer otherwise. Anything execution answers as a server error (or that cannot be classified) is rethrown, never reported as valid.</summary>
    Task<SqlCompileParityFailure?> CheckAsync(string sql, ClaimsPrincipal principal, string? dataSourceName, CancellationToken ct);
}

/// <summary>The refusal of the compile: the stable code and fixed message of execution, plus its position, named subject (function, construct or column) and hint.</summary>
public sealed record SqlCompileParityFailure(
    string Code,
    string Message,
    int? Line,
    int? Column,
    string? SubjectKind,
    string? SubjectName,
    string? Hint);
