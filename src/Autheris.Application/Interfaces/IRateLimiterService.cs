using Autheris.Domain.Model;
using Autheris.Domain.Options;

namespace Autheris.Application.Interfaces;

public readonly record struct RateLimitResult(bool Allowed, int RetryAfterSeconds);

public interface IRateLimiterService
{
    Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default);
    Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default);
    Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default);
}
