namespace Autheris.Application.Common.Quota;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;

public enum QueryCostOutcome
{
    Admitted,
    OverCost,
    QuotaExhausted,
    Unmetered
}

public sealed record QueryCostRequest(
    ClaimsPrincipal? Principal,
    string? ApiKey,
    string? ClientIp,
    int EstimatedCost,
    int TransportMaxCost,
    bool CapByTierMaxCostPerQuery);

public sealed record QueryCostAdmission(
    QueryCostOutcome Outcome,
    string SubjectId,
    ClientQuotaPolicy Policy,
    int ReservedCost,
    int MaxAllowedCost,
    int RemainingTokens,
    int RetryAfterSeconds,
    string? Scope)
{
    private int _settled;

    public bool TryMarkSettled() => Interlocked.CompareExchange(ref _settled, 1, 0) == 0;
}

public interface IQueryCostQuota
{
    Task<QueryCostAdmission> AdmitAsync(QueryCostRequest request, CancellationToken ct);
    Task<CostQuotaResult?> SettleAsync(QueryCostAdmission admission, int actualCost, CancellationToken ct);
    Task ChargeFailureFeeAsync(ClaimsPrincipal principal, CancellationToken ct);
}
