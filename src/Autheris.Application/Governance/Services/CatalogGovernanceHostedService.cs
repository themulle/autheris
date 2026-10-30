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
/// Issue #111 (A-9, rev. 3 b): audits the effective <c>Gateway:Catalog:DefaultVisibility</c> and <c>Gateway:Tenancy:DefaultTenantId</c> presence once at startup and on
/// every configuration change, turns the tally of tables created with a non-Hidden default tier into one aggregated audit entry per interval
/// (count, source; no table names), and keeps the tier gauge fresh. All writes are best effort and never block the host.
/// </summary>
public sealed class CatalogGovernanceHostedService : BackgroundService
{
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);

    private readonly IAuditWriter _audit;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly CatalogVisibilityMetrics _metrics;
    private readonly ILogger<CatalogGovernanceHostedService> _logger;
    private IDisposable? _subscription;
    private string _lastAudited = string.Empty;

    public CatalogGovernanceHostedService(
        IAuditWriter audit,
        IOptionsMonitor<GatewayOptions> options,
        CatalogVisibilityMetrics metrics,
        ILogger<CatalogGovernanceHostedService> logger)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _metrics.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await AuditConfigurationAsync("startup", _options.CurrentValue, cancellationToken).ConfigureAwait(false);
        _subscription = _options.OnChange(o => _ = OnOptionsChangedAsync(o));
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await FlushDefaultTierTallyAsync(CancellationToken.None).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushDefaultTierTallyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    internal Task OnOptionsChangedAsync(GatewayOptions options) => AuditConfigurationAsync("configuration", options, CancellationToken.None, onlyWhenChanged: true);

    private async Task AuditConfigurationAsync(string source, GatewayOptions options, CancellationToken ct, bool onlyWhenChanged = false)
    {
        var defaultVisibility = options.Catalog.DefaultVisibility.ToString();
        var mode = TenantResolver.EffectiveDefaultOf(options.Tenancy) is null ? "None" : "Configured";
        var state = defaultVisibility + "|" + mode;
        if (onlyWhenChanged && string.Equals(state, _lastAudited, StringComparison.Ordinal))
        {
            return;
        }

        _lastAudited = state;
        await WriteAsync(source, new { source, defaultVisibility, defaultTenant = mode }, ct).ConfigureAwait(false);
    }

    /// <summary>Writes one aggregated entry for the tables created since the last flush with a non-Hidden default tier; nothing when there are none.</summary>
    internal async Task FlushDefaultTierTallyAsync(CancellationToken ct)
    {
        var count = CatalogDefaultTierTally.TakeAndReset();
        if (count <= 0)
        {
            return;
        }

        await WriteAsync("ingestion", new
        {
            source = "ingestion",
            count,
            defaultVisibility = _options.CurrentValue.Catalog.DefaultVisibility.ToString()
        }, ct).ConfigureAwait(false);
        _metrics.RequestRefresh();
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
