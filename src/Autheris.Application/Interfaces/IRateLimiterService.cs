using Autheris.Domain.Model;
using Autheris.Domain.Options;

namespace Autheris.Application.Interfaces;

public readonly record struct RateLimitResult(bool Allowed, int RetryAfterSeconds);

public record BucketStateResult(int Capacity, double CurrentBalance, double RefillRatePerSecond);

public sealed record CallerBucketState(int Capacity, double CurrentBalance, double RefillRatePerSecond) : BucketStateResult(Capacity, CurrentBalance, RefillRatePerSecond);

public interface IRateLimiterService
{
    Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default);
    Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default);
    Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default);
    Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default);
    Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default);
    Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default);
    Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default);
}
