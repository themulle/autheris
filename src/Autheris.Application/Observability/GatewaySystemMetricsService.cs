namespace Autheris.Application.Observability;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.ResourceGroups;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class GatewaySystemMetricsService : IGatewaySystemMetricsService
{
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();
    private static readonly string GatewayVersion = typeof(GatewaySystemMetricsService).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    private readonly IResourceGroupManager _resourceGroupManager;
    private readonly IDbtHealthCircuitBreaker? _dbtCircuitBreaker;
    private readonly IGatewayHealthCheckService? _healthCheckService;
    private readonly ILogger<GatewaySystemMetricsService> _logger;

    public GatewaySystemMetricsService(
        IResourceGroupManager resourceGroupManager,
        ILogger<GatewaySystemMetricsService> logger,
        IDbtHealthCircuitBreaker? dbtCircuitBreaker = null,
        IGatewayHealthCheckService? healthCheckService = null)
    {
        _resourceGroupManager = resourceGroupManager;
        _logger = logger;
        _dbtCircuitBreaker = dbtCircuitBreaker;
        _healthCheckService = healthCheckService;
    }

    public async Task<GatewaySystemMetrics> CollectSystemMetricsAsync(CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var uptime = Stopwatch.GetElapsedTime(StartTimestamp);
        var memoryBytes = GC.GetTotalMemory(forceFullCollection: false);
        
        int threadCount;
        try
        {
            using var proc = Process.GetCurrentProcess();
            threadCount = proc.Threads.Count;
        }
        catch
        {
            threadCount = ThreadPool.ThreadCount;
        }

        var rgMetrics = _resourceGroupManager.GetMetrics();

        int quarantinedCount = 0;
        if (_dbtCircuitBreaker != null)
        {
            try
            {
                var healthStates = await _dbtCircuitBreaker.GetAllHealthStatesAsync(cancellationToken).ConfigureAwait(false);
                quarantinedCount = healthStates.Values.Count(s => s.Status == DbtModelHealthStatus.Quarantined);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query dbt circuit breaker states for system metrics");
            }
        }

        var components = new List<ComponentHealth>();
        if (_healthCheckService != null)
        {
            try
            {
                var healthReport = await _healthCheckService.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                foreach (var c in healthReport.Components)
                {
                    components.Add(new ComponentHealth(
                        ComponentName: c.Name,
                        Status: c.IsHealthy ? "Healthy" : "Degraded",
                        Details: c.Description,
                        CheckedAt: timestamp
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query health check service for system metrics");
                components.Add(new ComponentHealth("HealthCheckService", "Unhealthy", ex.Message, timestamp));
            }
        }
        else
        {
            components.Add(new ComponentHealth("CoreGateway", "Healthy", "Operating normally", timestamp));
        }

        // Add ResourceGroup component health (dynamically reports Degraded if any tier's queue is saturated)
        var isRgDegraded = rgMetrics.Tiers.Any(t => t.QueuedRequests >= t.MaxQueueDepth && t.MaxQueueDepth > 0);
        components.Add(new ComponentHealth(
            ComponentName: "ResourceGroups",
            Status: isRgDegraded ? "Degraded" : "Healthy",
            Details: $"Tiers: {rgMetrics.Tiers.Count} configured, Saturated: {isRgDegraded}",
            CheckedAt: timestamp
        ));

        return new GatewaySystemMetrics(
            Timestamp: timestamp,
            Version: GatewayVersion,
            Uptime: uptime,
            MemoryAllocatedBytes: memoryBytes,
            ThreadCount: threadCount,
            PolicyEpoch: 1L,
            QuarantinedModelsCount: quarantinedCount,
            ResourceGroups: rgMetrics,
            Components: components
        );
    }
}
