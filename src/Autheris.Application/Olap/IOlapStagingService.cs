namespace Autheris.Application.Olap;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Security;

/// <summary>The parsed body of an OLAP query request.</summary>
public sealed record OlapQueryCommand(string Sql, IReadOnlyList<string>? TableNames = null, int? Limit = null);

/// <summary>
/// Outcome of an OLAP execution: <see cref="Result"/> is set only for <c>200</c>; every failure carries the status code and the
/// client-facing <see cref="Error"/> (and <see cref="Details"/> when the host allows exposing them).
/// </summary>
public sealed record OlapStagingOutcome(int StatusCode, string? Error = null, string? Details = null, OlapQueryResult? Result = null)
{
    public bool IsSuccess => Result != null;
}

/// <summary>
/// WP-ARC-B-07: stages the requested tables (allowlist, access decision, governed read, budgets, required audits) and runs the
/// OLAP query. The endpoint keeps HTTP concerns only.
/// </summary>
public interface IOlapStagingService
{
    Task<OlapStagingOutcome> ExecuteAsync(
        ClaimsPrincipal user,
        SecurityPrincipalContext caller,
        OlapQueryCommand command,
        bool isDevelopment,
        string traceId,
        CancellationToken ct);
}
