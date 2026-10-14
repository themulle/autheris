using Autheris.Application.Hosting;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Extensions.OpenMetadata;

/// <summary>
/// Periodic OpenMetadata permission sync, run by exactly one cluster replica at a time under a renewed lease.
/// The sync is idempotent and therefore not fenced: a replica that loses its lease stops through the job's cancellation token.
/// </summary>
public sealed class OpenMetadataSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<GatewayOptions> options,
    IDistributedClusterStateProvider clusterState,
    ILogger<OpenMetadataSyncBackgroundService> logger,
    TimeProvider? timeProvider = null)
    : ClusterScheduledService<OpenMetadataSyncJob>(scopeFactory, clusterState, logger, timeProvider)
{
    protected override string LeaseKey => "catalog:openmetadata:sync";

    protected override ClusterSchedule GetSchedule()
    {
        var openMetadata = options.CurrentValue.OpenMetadata;
        var interval = TimeSpan.FromMinutes(Math.Clamp(openMetadata.SyncIntervalMinutes, 1, 1440));
        return new ClusterSchedule(
            openMetadata.Enabled,
            interval,
            InitialDelay: TimeSpan.Zero,
            LeaseTtl: TimeSpan.FromMinutes(2),
            MaxJitter: interval / 10);
    }
}

/// <summary>One OpenMetadata permission synchronization run.</summary>
public sealed class OpenMetadataSyncJob(IOpenMetadataSyncService syncService) : IClusterScheduledJob
{
    public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await syncService.SyncPermissionsAsync(dryRun: false, ct: ct).ConfigureAwait(false);
    }
}
