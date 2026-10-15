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
    /// WP-SC-16: registers <see cref="IGrantPolicy"/>; the host binds <see cref="IndividualGrantOptions"/> (a realm profile can only shorten the built-in maxima). The read-time clamp limits of <see cref="IndividualGrantLimits"/> follow the same options.
    /// </summary>
    public static IServiceCollection AddIndividualGrantPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.PostConfigure<IndividualGrantOptions>(o => IndividualGrantLimits.Configure(
            TimeSpan.FromDays(o.RegularMaxDays),
            TimeSpan.FromDays(o.UnmaskedMaxDays),
            TimeSpan.FromDays(o.ServicePrincipalMaxDays)));
        services.TryAddSingleton<IGrantPolicy>(sp => new GrantPolicy(sp.GetRequiredService<IOptions<IndividualGrantOptions>>()));
        return services;
    }
}
