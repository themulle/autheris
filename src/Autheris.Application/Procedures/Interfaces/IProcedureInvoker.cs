namespace Autheris.Application.Procedures.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Security context values passed to the database session (never client-controlled).</summary>
public sealed record ProcedureSecurityContext(string TenantId, string UserSid, string? Purpose);

/// <summary>
/// Ungoverned first result set returned by the invoker. <paramref name="MoreRowsAvailable"/> is true when at least one
/// further raw row existed after the requested row budget (never a count, never a value).
/// </summary>
public sealed record RawProcedureResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool MoreRowsAvailable);

/// <summary>Outcome of a write procedure call: the affected-row count the driver reported (null when it reports none), never a value.</summary>
public sealed record ProcedureWriteOutcome(long? RowsAffected);

/// <summary>F-SQL-02: Executes a validated procedure call on the database (database specific).</summary>
public interface IProcedureInvoker
{
    /// <summary>
    /// Reads at most <paramref name="rowBudget"/> rows (must be positive; there is no fallback to a configured limit).
    /// The caller decides the budget before the call.
    /// </summary>
    Task<RawProcedureResult> ExecuteReadAsync(
        ProcedureDefinition definition,
        IReadOnlyDictionary<string, object?> clientValues,
        ProcedureSecurityContext security,
        int rowBudget,
        CancellationToken ct);

    /// <summary>
    /// Calls a write procedure with typed parameters only and no result set. Autheris adds no read-only flag and no connection restriction;
    /// the database rights of the configured connection are the only technical limit. An invoker without write support refuses (fail closed).
    /// </summary>
    Task<ProcedureWriteOutcome> ExecuteWriteAsync(
        ProcedureDefinition definition,
        IReadOnlyDictionary<string, object?> clientValues,
        ProcedureSecurityContext security,
        CancellationToken ct) => throw new System.NotSupportedException("This procedure invoker does not execute write procedures.");
}
