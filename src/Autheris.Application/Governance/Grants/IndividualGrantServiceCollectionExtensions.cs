namespace Autheris.Application.Governance.Grants;

using System;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

public static class IndividualGrantServiceCollectionExtensions
{
    /// <summary>
    /// registers<see cref="IGrantPolicy"/> and <see cref="IIndividualGrantLimits"/>;
    /// the host binds <see cref="IndividualGrantOptions"/> (optional operator limits; no compiled-in ceilings).
    /// The read-time clamp limits of <see cref="IndividualGrantLimits"/> follow the same options.
    /// </summary>
    public static IServiceCollection AddIndividualGrantPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<IndividualGrantOptions>()
            .ValidateOnStart();
        services.TryAddSingleton<IIndividualGrantLimits, IndividualGrantLimitsService>();
        services.PostConfigure<IndividualGrantOptions>(o =>
        {
            var service = new IndividualGrantLimitsService(o);
            IndividualGrantLimits.Current = service;
        });

        services.TryAddSingleton<IGrantPolicy>(sp => new GrantPolicy(
            sp.GetRequiredService<IOptions<IndividualGrantOptions>>(),
            sp.GetService<IGrantValidityPolicy>(),
            sp.GetService<IOptions<AccessPolicyOptions>>()));
        return services;
    }
}
