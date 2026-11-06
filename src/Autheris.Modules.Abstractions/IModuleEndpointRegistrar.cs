namespace Autheris.Modules.Abstractions;

using Microsoft.AspNetCore.Routing;

/// <summary>
/// Guarded endpoint registrar supplied to modules during <see cref="IAutherisModule.MapEndpoints"/>.
/// Applies mandatory authorization, rate limiting, audit, and ingress transport metadata.
/// </summary>
public interface IModuleEndpointRegistrar
{
    /// <summary>
    /// Creates a route group with the specified prefix and pre-attached security, audit, and rate-limiting conventions.
    /// </summary>
    RouteGroupBuilder MapGroup(string prefix);
}
