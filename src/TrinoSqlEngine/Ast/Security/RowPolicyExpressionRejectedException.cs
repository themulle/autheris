namespace TrinoSqlEngine.Ast.Security;

using System.Security;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// Raised when the row-policy expression guard refuses an expression or type coercion on a policy-protected table
/// (<see cref="GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed"/>).
/// Preserves the fixed, data-free <see cref="RowPolicyErrorGuard.Message"/> while exposing a stable machine-readable code
/// and a safe remediation hint naming non-raising alternatives (<c>TRY_CAST</c>, <c>NULLIF</c>).
/// </summary>
public sealed class RowPolicyExpressionRejectedException : SecurityException
{
    public const string SafeHint =
        "Use non-raising expressions such as TRY_CAST(...) for type conversions and NULLIF(..., 0) to avoid division errors on tables with row or tenant policies.";

    public string Code => GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed;

    public string Hint => SafeHint;

    public RowPolicyExpressionRejectedException()
        : base(RowPolicyErrorGuard.Message)
    {
    }
}
