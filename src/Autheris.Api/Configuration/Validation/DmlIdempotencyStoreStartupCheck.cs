namespace Autheris.Api.Configuration.Validation;

using System.ComponentModel.DataAnnotations;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Aborts startup when data-changing WebSQL statements are enabled but the registered idempotency store is not shared by all replicas.
/// </summary>
public sealed class DmlIdempotencyStoreStartupCheck(IOptions<GatewayOptions> options, IIdempotencyStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.WebSql.AllowDml && store is not IClusterWideIdempotencyStore)
        {
            throw new ValidationException(
                "Configuration error: WebSql.AllowDml=true requires a cluster-wide idempotency store (Caching.Redis.Enabled = true) " +
                "so that DML idempotency keys are enforced across replicas.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
