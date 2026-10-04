namespace Autheris.Application.Performance.IncrementalDelivery;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-PERF-12: Lifecycle and Concurrency Manager for Incremental Delivery Streams.
/// Prevents Slowloris connection starvation and bounds concurrent multipart streaming channels.
/// </summary>
public interface IIncrementalDeliveryManager
{
    bool IsEnabled { get; }

    bool TryAcquireStreamSlot(string clientKey);

    void ReleaseStreamSlot(string clientKey);

    CancellationTokenSource CreateStreamTimeoutCts(CancellationToken clientDisconnectToken);

    int MaxChunks { get; }
}

public sealed class IncrementalDeliveryManager : IIncrementalDeliveryManager
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<IncrementalDeliveryManager> _logger;
    private readonly ConcurrentDictionary<string, int> _activeStreams = new(StringComparer.OrdinalIgnoreCase);

    public IncrementalDeliveryManager(
        IOptions<GatewayOptions> options,
        ILogger<IncrementalDeliveryManager> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsEnabled => _options.Value.IncrementalDelivery.Enabled;
    public int MaxChunks => _options.Value.IncrementalDelivery.MaxIncrementalChunks;

    public bool TryAcquireStreamSlot(string clientKey)
    {
        var max = _options.Value.IncrementalDelivery.MaxConcurrentStreamsPerClient;
        var current = _activeStreams.AddOrUpdate(clientKey, 1, (_, count) => count + 1);

        if (current > max)
        {
            _activeStreams.AddOrUpdate(clientKey, 0, (_, count) => Math.Max(0, count - 1));
            _logger.LogWarning("F-PERF-12 Stream rejected: Client {Client} exceeded max concurrent streams ({Current}/{Max})", clientKey, current, max);
            return false;
        }

        return true;
    }

    public void ReleaseStreamSlot(string clientKey)
    {
        _activeStreams.AddOrUpdate(clientKey, 0, (_, count) => Math.Max(0, count - 1));
    }

    public CancellationTokenSource CreateStreamTimeoutCts(CancellationToken clientDisconnectToken)
    {
        var timeoutMs = _options.Value.IncrementalDelivery.MaxDeferredExecutionTimeMs;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(clientDisconnectToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        return cts;
    }
}
