using System.Threading;
using System.Threading.Tasks;

namespace Autheris.Application.Audit;

public interface IAuthFailureAggregator
{
    Task RecordFailureAsync(
        string sourceIp,
        string reasonCode,
        string? principal = null,
        string? tenantId = null,
        CancellationToken ct = default);

    Task RecordRateLimitAsync(
        string sourceIp,
        string? sid = null,
        string? tenantId = null,
        CancellationToken ct = default);

    Task FlushAsync(CancellationToken ct = default);
}
