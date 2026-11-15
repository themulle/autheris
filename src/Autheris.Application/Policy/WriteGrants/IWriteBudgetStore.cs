namespace Autheris.Application.Policy.WriteGrants;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Configured write execution quotas and statement limits for a single governed DML call.
/// </summary>
/// <param name="StatementsPerMinute">Maximum DML statements per minute allowed for the caller within the tenant.</param>
/// <param name="RowsPerHour">Maximum cumulative rows affected per hour allowed for the caller within the tenant.</param>
/// <param name="GrantMaxRowsPerStatement">Optional per-statement cap from the matched write grant.</param>
/// <param name="ConfiguredMaxAffectedRows">Configured global <c>WebSql:MaxAffectedRows</c> limit.</param>
public sealed record WriteBudgetLimit(
    int StatementsPerMinute,
    long RowsPerHour,
    long? GrantMaxRowsPerStatement,
    long ConfiguredMaxAffectedRows)
{
    /// <summary>
    /// Computes the effective per-statement row cap by threading <see cref="GrantMaxRowsPerStatement"/> into <see cref="ConfiguredMaxAffectedRows"/>.
    /// </summary>
    public long EffectiveStatementMaxRows()
    {
        long maxAffected = ConfiguredMaxAffectedRows;
        if (GrantMaxRowsPerStatement is long grantCap && grantCap > 0)
        {
            maxAffected = maxAffected > 0 ? Math.Min(maxAffected, grantCap) : grantCap;
        }

        return maxAffected;
    }
}

/// <summary>
/// Outcome of attempting to acquire a statement slot from <see cref="IWriteBudgetStore"/>.
/// </summary>
/// <param name="Allowed">True when a statement slot was reserved and hourly row capacity remains.</param>
/// <param name="RemainingRowsInHour">Remaining affected-row capacity in the current hour window before this statement runs.</param>
/// <param name="EffectiveMaxRowsPerStatement">Effective statement row cap with write-grant limits applied.</param>
/// <param name="RetryAfterSeconds">Recommended delay in seconds before retrying when <paramref name="Allowed"/> is false.</param>
/// <param name="DenyReason">Stable machine-readable reason when rejected.</param>
public sealed record WriteBudgetAcquisition(
    bool Allowed,
    long RemainingRowsInHour,
    long EffectiveMaxRowsPerStatement,
    int RetryAfterSeconds,
    string? DenyReason = null);

/// <summary>
/// Enforces per-(tenant, subject) DML statement-per-minute and row-per-hour write budgets.
/// </summary>
public interface IWriteBudgetStore
{
    /// <summary>
    /// Checks remaining hourly row capacity and consumes one statement slot in the current minute window when allowed.
    /// </summary>
    Task<WriteBudgetAcquisition> TryAcquireStatementAsync(
        TenantId tenantId,
        string subjectSid,
        WriteBudgetLimit limit,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts to record <paramref name="affectedRows"/> against the caller's hourly row budget prior to committing the transaction.
    /// Returns false without consuming rows when recording would exceed <paramref name="rowsPerHourLimit"/>.
    /// </summary>
    Task<bool> TryRecordAffectedRowsAsync(
        TenantId tenantId,
        string subjectSid,
        long affectedRows,
        long rowsPerHourLimit,
        CancellationToken ct = default);
}
