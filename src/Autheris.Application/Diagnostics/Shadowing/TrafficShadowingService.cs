namespace Autheris.Application.Diagnostics.Shadowing;

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-OPS-01: Zero-impact asynchronous traffic shadowing worker.
/// Replays sanitized read queries against staging/canary targets over a bounded channel.
/// </summary>
public sealed class TrafficShadowingService : ITrafficShadowingService, IHostedService, IDisposable
{
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TrafficShadowingService> _logger;

    private readonly Channel<ShadowRequest> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;

    private long _enqueuedCount;
    private long _droppedCount;
    private long _replayedCount;

    public TrafficShadowingService(
        IOptions<GatewayOptions> gatewayOptions,
        HttpClient httpClient,
        ILogger<TrafficShadowingService> logger)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var capacity = Math.Max(100, _gatewayOptions.Value.TrafficShadowing.ChannelCapacity);
        var channelOptions = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<ShadowRequest>(channelOptions);
    }

    public bool IsEnabled => _gatewayOptions.Value.TrafficShadowing.Enabled;
    public long EnqueuedRequestsCount => Interlocked.Read(ref _enqueuedCount);
    public long DroppedRequestsCount => Interlocked.Read(ref _droppedCount);
    public long ReplayedRequestsCount => Interlocked.Read(ref _replayedCount);

    public bool ShouldSample()
    {
        var options = _gatewayOptions.Value.TrafficShadowing;
        if (!options.Enabled)
        {
            return false;
        }

        if (options.SampleRatePercentage <= 0)
        {
            return false;
        }

        if (options.SampleRatePercentage >= 100.0)
        {
            return true;
        }

        return Random.Shared.NextDouble() * 100.0 < options.SampleRatePercentage;
    }

    public bool EnqueueShadowRequest(ShadowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsEnabled)
        {
            return false;
        }

        // Apply PII redaction prior to queueing
        var options = _gatewayOptions.Value.TrafficShadowing;
        var sanitizedHeaders = PiiShadowingRedactor.RedactHeaders(request.Headers, options.StripPiiHeaders);
        var sanitizedBody = PiiShadowingRedactor.RedactBody(request.Body);

        var sanitizedRequest = request with
        {
            PathAndQuery = PiiShadowingRedactor.RedactPathAndQuery(request.PathAndQuery),
            Headers = sanitizedHeaders,
            Body = sanitizedBody
        };

        if (_channel.Writer.TryWrite(sanitizedRequest))
        {
            Interlocked.Increment(ref _enqueuedCount);
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        _logger.LogWarning("F-OPS-01 Shadowing channel full (capacity {Cap}); dropped request.", options.ChannelCapacity);
        return false;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsEnabled)
        {
            _logger.LogInformation(
                "F-OPS-01 Traffic Shadowing started targeting '{Target}' (sample rate: {Rate}%)",
                _gatewayOptions.Value.TrafficShadowing.TargetBaseUrl,
                _gatewayOptions.Value.TrafficShadowing.SampleRatePercentage);

            _workerTask = ProcessChannelAsync(_cts.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        if (_workerTask != null)
        {
            await Task.WhenAny(_workerTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task ProcessChannelAsync(CancellationToken ct)
    {
        var options = _gatewayOptions.Value.TrafficShadowing;
        var targetBaseUri = new Uri(options.TargetBaseUrl.TrimEnd('/'));

        while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var request))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await ReplayRequestAsync(request, targetBaseUri, options.TimeoutMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ReplayRequestAsync(
        ShadowRequest request,
        Uri targetBaseUri,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            var targetUri = new Uri(targetBaseUri, request.PathAndQuery.TrimStart('/'));
            using var msg = new HttpRequestMessage(new HttpMethod(request.Method), targetUri);

            foreach (var (headerKey, headerVal) in request.Headers)
            {
                if (string.Equals(headerKey, "Host", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(headerKey, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(headerKey, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                msg.Headers.TryAddWithoutValidation(headerKey, headerVal);
            }

            if (request.Body != null)
            {
                msg.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var sw = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(msg, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();

            Interlocked.Increment(ref _replayedCount);

            _logger.LogDebug(
                "F-OPS-01 Shadow replay to {Uri} completed in {Elapsed}ms (Status: {StatusCode})",
                targetUri, sw.ElapsedMilliseconds, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("F-OPS-01 Shadow replay timed out after {Timeout}ms.", timeoutMs);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "F-OPS-01 Failed to shadow request to staging target.");
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
