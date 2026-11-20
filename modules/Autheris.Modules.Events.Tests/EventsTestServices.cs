namespace Autheris.Modules.Events.Tests;

using Autheris.Api.Hosting;
using Autheris.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the host <see cref="IModuleRequestContext"/> the way the host does, so endpoint handlers resolve it.</summary>
internal static class EventsTestServices
{
    public static IServiceCollection AddRequestContext(this IServiceCollection services) =>
        services.AddSingleton<IModuleRequestContext, ModuleRequestContext>();

    public static IServiceProvider RequestContextOnly() => new ServiceCollection().AddRequestContext().BuildServiceProvider();
}
