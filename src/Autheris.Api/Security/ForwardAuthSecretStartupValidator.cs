using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Security;

/// <summary>
/// R-DEP-1: the ForwardAuth shared secret from <c>SharedSecretKeyVaultRef</c> was resolved and length-checked only per
/// request. Outside Development it is now resolved at startup, so a missing or short secret stops the start.
/// </summary>
public sealed class ForwardAuthSecretStartupValidator(
    IOptions<GatewayOptions> options,
    IHostEnvironment environment,
    IKeyVaultSecretProvider? secretProvider = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var forwardAuth = options.Value.Authentication.ForwardAuth;
        if (environment.IsDevelopment() || !forwardAuth.Enabled || string.IsNullOrWhiteSpace(forwardAuth.SharedSecretKeyVaultRef))
        {
            return Task.CompletedTask;
        }

        if (secretProvider == null)
        {
            throw new InvalidOperationException("Sicherheitsfehler: ForwardAuth SharedSecretKeyVaultRef ist konfiguriert, aber kein IKeyVaultSecretProvider registriert.");
        }

        SecretKeyRequirements.EnsureMinimumLength(
            secretProvider.GetSecretBytes(forwardAuth.SharedSecretKeyVaultRef),
            "Das ForwardAuth-Shared-Secret (SharedSecretKeyVaultRef)",
            isDevelopment: false);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
