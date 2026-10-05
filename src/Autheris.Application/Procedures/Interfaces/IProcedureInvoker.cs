namespace Autheris.Application.Procedures.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Security context values passed to the database session (never client-controlled).</summary>
public sealed record ProcedureSecurityContext(string TenantId, string UserSid, string? Purpose);

/// <summary>Ungoverned first result set returned by the invoker.</summary>
public sealed record RawProcedureResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated);

/// <summary>F-SQL-02: Executes a validated procedure call on the database (database specific).</summary>
public interface IProcedureInvoker
{
    Task<RawProcedureResult> ExecuteReadAsync(
        ProcedureDefinition definition,
        IReadOnlyDictionary<string, object?> clientValues,
        ProcedureSecurityContext security,
        CancellationToken ct);
}
