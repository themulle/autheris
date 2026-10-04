namespace Autheris.Extensions;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Idempotency guard for the per-integration registration methods: every <c>Add*Integration</c> method registers its
/// services exactly once, even if it is invoked multiple times (e.g. directly and via <c>AddGatewayExtensions</c>).
/// </summary>
internal static class ExtensionRegistration
{
    internal static bool TryBegin<TMarker>(IServiceCollection services)
        where TMarker : class, new()
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(TMarker))
            {
                return false;
            }
        }

        services.AddSingleton(new TMarker());
        return true;
    }
}
