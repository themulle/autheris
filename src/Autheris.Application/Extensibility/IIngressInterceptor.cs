namespace Autheris.Application.Extensibility;

public interface IIngressInterceptor
{
    int Order => 0;
    ValueTask<IngressResult> OnIngressAsync(IngressContext context, CancellationToken cancellationToken = default);
}
