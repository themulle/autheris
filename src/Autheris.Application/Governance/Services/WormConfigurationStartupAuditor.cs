namespace Autheris.Application.Governance.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Startup bootstrap for the WORM configuration audit chain: mints policy epoch 1 (chained to the
/// all-zero genesis hash) once when the host starts, so the current configuration hash is always initialized.
/// </summary>
public sealed class WormConfigurationStartupAuditor : IHostedService
{
    private readonly IWormConfigurationAuditService _auditService;
    private readonly ILogger<WormConfigurationStartupAuditor> _logger;

    public WormConfigurationStartupAuditor(
        IWormConfigurationAuditService auditService,
        ILogger<WormConfigurationStartupAuditor> logger)
    {
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var record = await _auditService.AuditConfigurationSnapshotAsync("STARTUP", "SYSTEM", cancellationToken);
        _logger.LogInformation("WORM configuration startup audit completed (record minted: {Minted})", record is not null);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
