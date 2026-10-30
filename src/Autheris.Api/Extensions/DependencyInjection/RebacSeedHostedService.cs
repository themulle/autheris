namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Audit;
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
    private readonly IAuditWriter _auditWriter;
    private readonly IHostEnvironment? _environment;
    private readonly Autheris.Application.Tenancy.ITenantResolver? _tenantResolver;

    public RebacSeedHostedService(
        IRebacStore store,
        IOptions<GatewayOptions> options,
        ILogger<RebacSeedHostedService> logger,
        IAuditWriter auditWriter,
        IHostEnvironment? environment = null,
        Autheris.Application.Tenancy.ITenantResolver? tenantResolver = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _environment = environment;
        _tenantResolver = tenantResolver;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var tuplesToSeed = new List<RebacTuple>(_options.Value.Rebac.SeedTuples);
        if (tuplesToSeed.Count == 0 && _environment?.IsDevelopment() == true)
        {
            // DT-A-9: the demo tuples belong to the configured default tenant; without one nothing is seeded (no tenant is invented).
            if (_tenantResolver?.DefaultTenant is { } defaultTenant)
            {
                var d = defaultTenant.Value;
                tuplesToSeed.AddRange([
                    new(d, "david", "viewer", "table:lakehouse.dbo.orders"),
                    new(d, "S-1-5-21-LWE-DAVID", "viewer", "table:lakehouse.dbo.orders"),
                    new(d, "david", "viewer", "table:sales.public.orders"),
                    new(d, "S-1-5-21-LWE-DAVID", "viewer", "table:sales.public.orders"),
                    new(d, "david", "viewer", "table:sales.crm.contacts"),
                    new("tenant_lwe", "david", "viewer", "table:lakehouse.dbo.orders"),
                    new("tenant_lwe", "S-1-5-21-LWE-DAVID", "viewer", "table:lakehouse.dbo.orders")
                ]);
            }
            else
            {
                _logger.LogInformation("The development ReBAC demo tuples are not seeded: no default tenant is configured (Gateway:Tenancy:DefaultTenantId).");
            }
        }

        if (tuplesToSeed.Count > 0)
        {
            try
            {
                await _store.AddTuplesAsync(tuplesToSeed, cancellationToken).ConfigureAwait(false);
                await _auditWriter.RecordBestEffortAsync(new AuditLogEntry
                {
                    TenantId = TenantId.Platform, // startup audit event (DT-A-6)
                    EventType = AuditEventTypes.RebacTupleSeed,
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
