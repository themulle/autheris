namespace Autheris.Application.Tenancy;

using Autheris.Application.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>DT-A-10: the tenant resolver singleton and the default-tenant start-up audit.</summary>
    public static IServiceCollection AddAutherisTenancy(this IServiceCollection services)
    {
        services.TryAddSingleton<ITenantResolver>(sp => new TenantResolver(
            CurrentTenancy(sp.GetRequiredService<IOptionsMonitor<Domain.Options.GatewayOptions>>()),
            sp.GetService<IIdentityRealmRegistry>(),
            sp.GetRequiredService<ILogger<TenantResolver>>()));
        services.AddHostedService<TenancyDefaultTenantStartupAuditor>();
        return services;
    }

    private static Func<Domain.Options.TenancyOptions> CurrentTenancy(IOptionsMonitor<Domain.Options.GatewayOptions> monitor) =>
        () => monitor.CurrentValue.Tenancy;
}
