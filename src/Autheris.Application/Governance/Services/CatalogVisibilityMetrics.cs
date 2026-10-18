namespace Autheris.Application.Governance.Services;

using System.Diagnostics.Metrics;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Issue #111 (A-9): <c>autheris_catalog_visibility_total{tier}</c>, an observable gauge over cached counts. The cache is refreshed at start and
/// after every tier change; tags are only the three tier names (no table names or ids). A failing refresh keeps the last values.
/// </summary>
public sealed class CatalogVisibilityMetrics : ICatalogVisibilityMetrics
{
    private static long s_hidden;
    private static long s_summary;
    private static long s_full;

    // Process-wide (static) gauge; instances only update the cached values.
    private static readonly ObservableGauge<long> Gauge = GatewayDiagnostics.Meter.CreateObservableGauge(
        "autheris_catalog_visibility_total",
        () => new[]
        {
            new Measurement<long>(Interlocked.Read(ref s_hidden), new KeyValuePair<string, object?>("tier", "hidden")),
            new Measurement<long>(Interlocked.Read(ref s_summary), new KeyValuePair<string, object?>("tier", "summary")),
            new Measurement<long>(Interlocked.Read(ref s_full), new KeyValuePair<string, object?>("tier", "full"))
        },
        description: "Datasets per catalog visibility tier");

    private readonly ITableMetadataRepository _tables;
    private readonly ILogger<CatalogVisibilityMetrics> _logger;
    private int _refreshRunning;

    public CatalogVisibilityMetrics(ITableMetadataRepository tables, ILogger<CatalogVisibilityMetrics> logger)
    {
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ = Gauge;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var counts = await _tables.GetDatasetVisibilityCountsAsync(ct).ConfigureAwait(false);
            Interlocked.Exchange(ref s_hidden, counts.Hidden);
            Interlocked.Exchange(ref s_summary, counts.Summary);
            Interlocked.Exchange(ref s_full, counts.Full);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The catalog visibility gauge could not be refreshed; the last values are kept.");
        }
    }

    public void RequestRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
            }
        });
    }
}
