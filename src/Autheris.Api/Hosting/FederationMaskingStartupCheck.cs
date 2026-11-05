namespace Autheris.Api.Hosting;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// GHSA-xpw9-4m63-69pf: Validates that when Federation result masking is enabled, an ISubgraphResultMasker is registered.
/// </summary>
public sealed class FederationMaskingStartupCheck(
    IOptions<GatewayOptions> options,
    IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(options.Value, services);
        return Task.CompletedTask;
    }

    public static void Validate(GatewayOptions options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        if (options.Federation.Enabled && options.Federation.EnableResultMasking)
        {
            var masker = services.GetService<ISubgraphResultMasker>();
            if (masker == null)
            {
                throw new InvalidOperationException("Security violation: Federation result masking is enabled (Federation:EnableResultMasking = true), but no ISubgraphResultMasker is registered in the service provider.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
