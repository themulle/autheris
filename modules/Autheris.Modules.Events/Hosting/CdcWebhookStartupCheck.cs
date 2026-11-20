namespace Autheris.Api.Hosting;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Startup diagnostic of the CDC webhook feature: outside Development a missing or too short <c>cdc-webhook-kek</c> (env
/// <c>CDC_WEBHOOK_KEK</c>, at least 32 bytes) makes every subscription POST fail closed with 503, so the operator is warned early.
/// </summary>
public sealed class CdcWebhookStartupCheck(IWebhookSecretProtector protector, IHostEnvironment environment, ILogger<CdcWebhookStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() && !protector.IsAvailable)
        {
            logger.LogWarning(
                "CDC webhooks are enabled but the secret key 'cdc-webhook-kek' (CDC_WEBHOOK_KEK, at least 32 bytes) is not available: creating subscriptions will fail with 503 until it is provisioned.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
