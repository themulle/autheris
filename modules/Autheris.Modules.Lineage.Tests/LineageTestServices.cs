namespace Autheris.Modules.Lineage.Tests;

using Autheris.Api.Hosting;
using Autheris.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

internal static class LineageTestServices
{
    public static IServiceCollection AddRequestContext(this IServiceCollection services)
    {
        services.TryAddSingleton<IModuleRequestContext, ModuleRequestContext>();
        return services;
    }
}
