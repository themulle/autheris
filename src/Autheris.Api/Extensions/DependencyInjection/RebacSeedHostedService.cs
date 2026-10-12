namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// AR-06 / SEC-Invariant 4: Asynchronously seeds ReBAC tuples during host startup without sync-over-async blocking,
/// recording the seed operation under fixed identity System:HostedService:RebacSeedHostedService.
/// </summary>
public sealed class RebacSeedHostedService : IHostedService
{
    private readonly IRebacStore _store;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<RebacSeedHostedService> _logger;
    private readonly IAuditLogRepository _auditLog;
    private readonly IHostEnvironment? _environment;

    public RebacSeedHostedService(
        IRebacStore store,
        IOptions<GatewayOptions> options,
        ILogger<RebacSeedHostedService> logger,
        IAuditLogRepository auditLog,
        IHostEnvironment? environment = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _environment = environment;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var tuplesToSeed = new List<RebacTuple>(_options.Value.Rebac.SeedTuples);
        if (tuplesToSeed.Count == 0 && _environment?.IsDevelopment() == true)
        {
            tuplesToSeed.AddRange([
                new("default", "user:david", "viewer", "table:lakehouse.dbo.orders"),
                new("default", "S-1-5-21-LWE-DAVID", "viewer", "table:lakehouse.dbo.orders"),
                new("default", "user:david", "viewer", "table:sales.public.orders"),
                new("default", "S-1-5-21-LWE-DAVID", "viewer", "table:sales.public.orders"),
                new("default", "user:david", "viewer", "table:sales.crm.contacts"),
                new("tenant_lwe", "user:david", "viewer", "table:lakehouse.dbo.orders"),
                new("tenant_lwe", "S-1-5-21-LWE-DAVID", "viewer", "table:lakehouse.dbo.orders")
            ]);
        }

        if (tuplesToSeed.Count > 0)
        {
            try
            {
                await _store.AddTuplesAsync(tuplesToSeed, cancellationToken).ConfigureAwait(false);
                await _auditLog.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = TenantId.LegacySingleTenant,
                    EventType = "REBAC_TUPLE_SEED",
                    ActorSid = new Sid("System:HostedService:RebacSeedHostedService"),
                    TargetTable = "RebacTuples",
                    Decision = "ALLOW",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = $"{{\"count\":{tuplesToSeed.Count}}}"
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to preload ReBAC seed tuples.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
