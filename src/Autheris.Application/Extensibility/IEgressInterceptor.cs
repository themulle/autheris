namespace Autheris.Application.Extensibility;

public interface IEgressInterceptor
{
    int Order => 0;
    ValueTask<EgressResult> OnEgressAsync(EgressContext context, CancellationToken cancellationToken = default);
}
