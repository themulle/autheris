namespace Autheris.Application.Procedures.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Governed result of a stored procedure call (first result set only).</summary>
public sealed record GovernedProcedureResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    bool Truncated,
    long ElapsedMilliseconds);

public interface IProcedureExecutionService
{
    Task<GovernedProcedureResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object?>? rawInputs,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// True if the caller may see the procedure: it is <see cref="ProcedureState.Active"/> and the visibility stage (identity, role
    /// gate, non-empty table set, consent on every table) passes. Any evaluation failure except cancellation yields false.
    /// Shared seam for the list, the OpenAPI document and later callers.
    /// </summary>
    Task<bool> IsVisibleAsync(RegisteredProcedure procedure, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default);
}

/// <summary>
/// The procedure raised a business error (THROW 50000-59999 or SQLSTATE P0001). Carries the error number or SQLSTATE only; the
/// database message text never leaves the invoker (it may contain data values), the message is fixed.
/// </summary>
public sealed class ProcedureBusinessException : System.Exception
{
    public const string FixedMessage = "The procedure rejected the request.";

    public ProcedureBusinessException(int? errorNumber, string? sqlState, string? traceId = null) : base(FixedMessage)
    {
        ErrorNumber = errorNumber;
        SqlState = sqlState;
        TraceId = traceId;
    }

    public int? ErrorNumber { get; }

    public string? SqlState { get; }

    /// <summary>Trace id shared with the audit rows of the call (random per request).</summary>
    public string? TraceId { get; }
}

/// <summary>The procedure endpoint exists but is disabled (validation failed or drift detected).</summary>
public sealed class ProcedureUnavailableException : System.Exception
{
    public ProcedureUnavailableException(string message) : base(message)
    {
    }
}
