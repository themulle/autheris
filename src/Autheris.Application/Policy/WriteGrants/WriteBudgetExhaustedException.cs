namespace Autheris.Application.Policy.WriteGrants;

using Autheris.Application.Sql;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// Raised when a caller's per-tenant DML statement-per-minute or row-per-hour write budget is exhausted (HTTP 429).
/// </summary>
public sealed class WriteBudgetExhaustedException : WebSqlPolicyException
{
    public int RetryAfterSeconds { get; }

    public WriteBudgetExhaustedException(string reason, int retryAfterSeconds = 60)
        : base($"Write budget exhausted ({reason}).", GovernedSqlErrorCodes.WriteBudgetExhausted)
    {
        RetryAfterSeconds = retryAfterSeconds > 0 ? retryAfterSeconds : 1;
    }
}
