namespace Autheris.Application.Extensibility;

public interface IExtensibilityPipeline
{
    ValueTask<IngressResult> ProcessIngressAsync(IngressContext context, CancellationToken cancellationToken = default);
    ValueTask<EgressResult> ProcessEgressAsync(EgressContext context, CancellationToken cancellationToken = default);
}
