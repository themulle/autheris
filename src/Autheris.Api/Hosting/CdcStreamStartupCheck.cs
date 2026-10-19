namespace Autheris.Api.Hosting;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Startup diagnostic of the governed CDC stream (PLAN-CDC-99 D-A8, SEC-CDC99-14): with MSSQL Change Tracking enabled, a missing, short or
/// unresolvable active stream KEK makes every table activation fail with <c>CDC_KEY_UNAVAILABLE</c>, so the operator is warned early.
/// Only the stable code is logged, never a secret name or key material.
/// </summary>
public sealed class CdcStreamStartupCheck(
    ICdcKeyEncryptionKeyProvider keks,
    IOptions<GatewayOptions> options,
    IHostEnvironment environment,
    ILogger<CdcStreamStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.MssqlChangeTracking.Enabled)
        {
            return Task.CompletedTask;
        }

        var active = keks.ActiveKekId;
        if (active is null || !keks.TryGetKek(active, out _))
        {
            logger.LogWarning(
                "CDC change tracking is enabled but no usable stream key-encryption key is available ({Code}): table activation will fail until Gateway:CdcStream:Encryption provides an active KEK secret of at least 32 bytes.",
                CdcActivationCodes.KeyUnavailable);
        }
        else if (environment.IsDevelopment() && options.Value.CdcStream.Encryption.Keks.Count == 0)
        {
            logger.LogWarning("CDC stream encryption uses an ephemeral development key: stream events become unreadable after a restart.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
