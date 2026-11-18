namespace Autheris.Application.Governance.Services;

using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Application.Tenancy;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Audits whether a default tenant is configured once at startup and on every configuration change. The write is best effort and never blocks the host.
/// </summary>
public sealed class CatalogGovernanceHostedService : BackgroundService
{
    private readonly IAuditWriter _audit;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ILogger<CatalogGovernanceHostedService> _logger;
    private IDisposable? _subscription;
    private string _lastAudited = string.Empty;

    public CatalogGovernanceHostedService(
        IAuditWriter audit,
        IOptionsMonitor<GatewayOptions> options,
        ILogger<CatalogGovernanceHostedService> logger)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await AuditConfigurationAsync("startup", _options.CurrentValue, cancellationToken).ConfigureAwait(false);
        _subscription = _options.OnChange(o => _ = OnOptionsChangedAsync(o));
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    internal Task OnOptionsChangedAsync(GatewayOptions options) => AuditConfigurationAsync("configuration", options, CancellationToken.None, onlyWhenChanged: true);

    private async Task AuditConfigurationAsync(string source, GatewayOptions options, CancellationToken ct, bool onlyWhenChanged = false)
    {
        var mode = TenantResolver.EffectiveDefaultOf(options.Tenancy) is null ? "None" : "Configured";
        if (onlyWhenChanged && string.Equals(mode, _lastAudited, StringComparison.Ordinal))
        {
            return;
        }

        _lastAudited = mode;
        await WriteAsync(source, new { source, defaultTenant = mode }, ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(string source, object details, CancellationToken ct)
    {
        try
        {
            await _audit.RecordBestEffortAsync(new AuditLogEntry
            {
                TenantId = TenantId.Platform, // tenant-less background audit (DT-A-6)
                EventType = AuditEventTypes.CatalogVisibilityChanged,
                ActorSid = new Sid("S-1-5-18"),
                TargetTable = string.Empty,
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(details)
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The catalog governance audit entry for source {Source} could not be written.", source);
        }
    }
}
