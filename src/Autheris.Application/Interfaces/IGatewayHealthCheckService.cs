namespace Autheris.Application.Interfaces;

public readonly record struct HealthCheckComponentResult(string Name, bool IsHealthy, string? Description = null);

public readonly record struct GatewayHealthReport(bool IsHealthy, IReadOnlyList<HealthCheckComponentResult> Components);

public interface IGatewayHealthCheckService
{
    Task<GatewayHealthReport> CheckHealthAsync(CancellationToken ct = default);
}
