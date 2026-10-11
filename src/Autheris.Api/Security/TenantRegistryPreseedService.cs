namespace Autheris.Api.Security;

using Autheris.Application.Security;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// CR-ADG-31: records the configured tenant ids in the registry at startup; configured collision groups are marked and denied (B-1).
/// A registry failure fails the start: without the seed a configured collision group would be unmarked.
/// </summary>
public sealed class TenantRegistryPreseedService(ITenantRegistry registry, IOptionsMonitor<GatewayOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await registry.PreseedAsync(TenantCollisionGuard.ConfiguredTenantIds(options.CurrentValue).OfType<string>().ToList(), cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
