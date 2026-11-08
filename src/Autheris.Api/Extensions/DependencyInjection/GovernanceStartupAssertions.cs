namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Audit;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Startup assertions about the governance store wiring .</summary>
public static class GovernanceStartupAssertions
{
    /// <summary>Resolves <see cref="IAuditChainAnchorStore"/> once at startup so a missing or unsupported anchor store fails before serving.</summary>
    public static void AssertAnchorStoreResolvable(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<IAuditChainAnchorStore>() is null)
        {
            throw new InvalidOperationException("Startup check failed: no IAuditChainAnchorStore is registered for the governance store.");
        }
    }
}
