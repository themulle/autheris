namespace Autheris.Application.Procedures.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

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
}

/// <summary>The procedure raised a business error (THROW 50000-59999). The message is safe to return (sanitized).</summary>
public sealed class ProcedureBusinessException : System.Exception
{
    public ProcedureBusinessException(string message) : base(message)
    {
    }
}

/// <summary>The procedure endpoint exists but is disabled (validation failed or drift detected).</summary>
public sealed class ProcedureUnavailableException : System.Exception
{
    public ProcedureUnavailableException(string message) : base(message)
    {
    }
}
