namespace Autheris.Application.Governance;

using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Microsoft.Extensions.DependencyInjection;

public static class AccessPlanningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the two-phase access planning and administrative control plane services.
    /// </summary>
    public static IServiceCollection AddAutherisGovernancePlans(this IServiceCollection services)
    {
        services.AddSingleton<IAccessPlanningService, AccessPlanningService>();
        return services;
    }
}
