namespace Autheris.Application.Extensibility;

using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ExtensibilityPipeline : IExtensibilityPipeline
{
    private readonly IReadOnlyList<IIngressInterceptor> _ingressInterceptors;
    private readonly IReadOnlyList<IEgressInterceptor> _egressInterceptors;
    private readonly ILogger<ExtensibilityPipeline> _logger;
    private readonly ExtensibilityPipelineOptions _options;

    public ExtensibilityPipeline(
        IEnumerable<IIngressInterceptor> ingressInterceptors,
        IEnumerable<IEgressInterceptor> egressInterceptors,
        ILogger<ExtensibilityPipeline> logger,
        IOptions<ExtensibilityPipelineOptions>? options = null)
    {
        _options = options?.Value ?? new ExtensibilityPipelineOptions();
        _ingressInterceptors = ingressInterceptors.OrderBy(i => i.Order).ToList();
        _egressInterceptors = egressInterceptors.OrderBy(i => i.Order).ToList();
        _logger = logger;
    }

    public async ValueTask<IngressResult> ProcessIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
    {
        foreach (var interceptor in _ingressInterceptors)
        {
            IngressResult result;
            try
            {
                result = await RunWithTimeoutAsync(
                    ct => interceptor.OnIngressAsync(context, ct).AsTask(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Ingress interceptor {Interceptor} failed (policy: {Policy})",
                    interceptor.GetType().Name, _options.IngressFailurePolicy);
                if (_options.IngressFailurePolicy == InterceptorFailurePolicy.FailOpen)
                {
                    continue;
                }

                return IngressResult.Deny("Request denied: an ingress interceptor failed.", 503);
            }

            if (result.Decision != IngressDecision.Continue)
            {
                _logger.LogInformation(
                    "Ingress interceptor {Interceptor} returned decision {Decision} (Status: {Status}, Reason: {Reason})",
                    interceptor.GetType().Name,
                    result.Decision,
                    result.StatusCode,
                    result.Reason);
                return result;
            }
        }

        return IngressResult.Continue();
    }

    public async ValueTask<EgressResult> ProcessEgressAsync(EgressContext context, CancellationToken cancellationToken = default)
    {
        var aggregatedResult = new EgressResult();

        foreach (var interceptor in _egressInterceptors)
        {
            EgressResult result;
            try
            {
                result = await RunWithTimeoutAsync(
                    ct => interceptor.OnEgressAsync(context, ct).AsTask(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Egress interceptor {Interceptor} failed (policy: {Policy})",
                    interceptor.GetType().Name, _options.EgressFailurePolicy);
                if (_options.EgressFailurePolicy == InterceptorFailurePolicy.FailOpen)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Egress interceptor '{interceptor.GetType().Name}' failed and the egress policy is FailClosed.", ex);
            }

            if (result.AdditionalHeaders.Count > 0)
            {
                foreach (var (k, v) in result.AdditionalHeaders)
                {
                    aggregatedResult.AdditionalHeaders[k] = v;
                    context.Headers[k] = v;
                }
            }

            if (result.Handled)
            {
                if (result.MutatedResponseText != null)
                {
                    context.ResponseBodyText = result.MutatedResponseText;
                }
                if (result.MutatedResponseBytes != null)
                {
                    context.ResponseBytes = result.MutatedResponseBytes;
                }
            }
        }

        return aggregatedResult;
    }

    private async Task<T> RunWithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(_options.InterceptorTimeoutMs);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            // WaitAsync also bounds interceptors that ignore the cancellation token.
            return await action(cts.Token).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Interceptor exceeded the time budget of {_options.InterceptorTimeoutMs} ms.");
        }
    }
}
