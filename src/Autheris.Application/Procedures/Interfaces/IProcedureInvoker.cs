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

/// <summary>F-SQL-02: Executes a validated procedure call on the database (database specific).</summary>
public interface IProcedureInvoker
{
    /// <summary>
    /// Reads at most <paramref name="rowBudget"/> rows (must be positive; there is no fallback to a configured limit).
    /// The caller decides the budget before the call (SEC-PROC152I2-01).
    /// </summary>
    Task<RawProcedureResult> ExecuteReadAsync(
        ProcedureDefinition definition,
        IReadOnlyDictionary<string, object?> clientValues,
        ProcedureSecurityContext security,
        int rowBudget,
        CancellationToken ct);
}
