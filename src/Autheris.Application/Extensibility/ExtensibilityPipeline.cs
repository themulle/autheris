namespace Autheris.Application.Extensibility;

using Microsoft.Extensions.Logging;

public sealed class ExtensibilityPipeline : IExtensibilityPipeline
{
    private readonly IReadOnlyList<IIngressInterceptor> _ingressInterceptors;
    private readonly IReadOnlyList<IEgressInterceptor> _egressInterceptors;
    private readonly ILogger<ExtensibilityPipeline> _logger;

    public ExtensibilityPipeline(
        IEnumerable<IIngressInterceptor> ingressInterceptors,
        IEnumerable<IEgressInterceptor> egressInterceptors,
        ILogger<ExtensibilityPipeline> logger)
    {
        _ingressInterceptors = ingressInterceptors.OrderBy(i => i.Order).ToList();
        _egressInterceptors = egressInterceptors.OrderBy(i => i.Order).ToList();
        _logger = logger;
    }

    public async ValueTask<IngressResult> ProcessIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
    {
        foreach (var interceptor in _ingressInterceptors)
        {
            var result = await interceptor.OnIngressAsync(context, cancellationToken);
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
            var result = await interceptor.OnEgressAsync(context, cancellationToken);
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
}
