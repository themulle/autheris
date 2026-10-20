namespace Autheris.Api.Hosting.Warmup;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.OData.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// SEC-PERF254-02 / WP-5: Pre-warms discovery documents by generating and caching global OpenAPI index document.
/// Strictly forbidden from accessing tenant credentials or writing governance/audit data.
/// </summary>
public sealed class DiscoveryDocumentWarmup : IStartupWarmup
{
    private readonly IDynamicOpenApiGenerator? _generator;
    private readonly ILogger<DiscoveryDocumentWarmup>? _logger;

    public DiscoveryDocumentWarmup(
        IDynamicOpenApiGenerator? generator = null,
        ILogger<DiscoveryDocumentWarmup>? logger = null)
    {
        _generator = generator;
        _logger = logger;
    }

    public string Name => StartupWarmupOptions.DiscoveryDocumentStep;

    public WarmupKind Kind => WarmupKind.BestEffort;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_generator == null)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await _generator.GetIndexDocumentAsync(baseUrl: null, cancellationToken).ConfigureAwait(false);
            _logger?.LogDebug("Discovery document index pre-warmed successfully.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Discovery document warm-up failed; continuing best-effort.");
        }
    }
}
