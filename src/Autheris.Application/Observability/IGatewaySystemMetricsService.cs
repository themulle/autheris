namespace Autheris.Application.Observability;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IGatewaySystemMetricsService
{
    Task<GatewaySystemMetrics> CollectSystemMetricsAsync(CancellationToken cancellationToken = default);
}
