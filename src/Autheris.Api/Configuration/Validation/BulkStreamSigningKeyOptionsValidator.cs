using System;
using System.Text;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// Validates <c>Gateway:RestApi:BulkStream:SigningKey</c>, the HMAC key of the keyset cursors of the bulk stream endpoints
/// (<c>/stream</c>, <c>/bulk</c>). Without a configured key every node signs with its own random per-process key, so a continuation
/// that reaches another node (or the same node after a restart) fails with a signature error. Outside Development the start therefore
/// fails when the endpoints are enabled and no key of at least 32 bytes is configured (a Key Vault or secret store value is supplied
/// through the configuration provider, so it arrives here as the configured value). In Development the ephemeral key stays a warning.
/// The key is never echoed.
/// </summary>
public sealed class BulkStreamSigningKeyOptionsValidator : IValidateOptions<GatewayOptions>
{
    private readonly IHostEnvironment _environment;
    private readonly ILogger<BulkStreamSigningKeyOptionsValidator>? _logger;

    public BulkStreamSigningKeyOptionsValidator(IHostEnvironment environment, ILogger<BulkStreamSigningKeyOptionsValidator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Same switch semantics as the endpoint mapping (RestApi:BulkStream:Enabled, else the REST data endpoint switch).
        if (!ReadPathSwitches.From(options).BulkStreamEnabled)
        {
            return ValidateOptionsResult.Success;
        }

        var key = options.RestApi?.BulkStream?.SigningKey;
        var configured = !string.IsNullOrWhiteSpace(key);

        if (_environment.IsDevelopment())
        {
            if (!configured)
            {
                _logger?.LogWarning(
                    "Gateway:RestApi:BulkStream:SigningKey is not configured; using a random per-process key. Keyset cursors of the bulk stream endpoints " +
                    "are only valid on this instance until restart. A key of at least {MinimumBytes} bytes is required outside Development.",
                    SecretKeyRequirements.MinimumKeyBytes);
            }

            return ValidateOptionsResult.Success;
        }

        if (!configured)
        {
            return ValidateOptionsResult.Fail(
                "Security violation: Gateway:RestApi:BulkStream:SigningKey is required outside Development while the bulk stream endpoints are enabled. " +
                "Without it every node signs keyset cursors with its own random key, so continuations across nodes or restarts fail. " +
                $"Configure a key of at least {SecretKeyRequirements.MinimumKeyBytes} bytes (the same on every node, for example from Key Vault), " +
                "or disable the endpoints with Gateway:RestApi:BulkStream:Enabled = false.");
        }

        if (Encoding.UTF8.GetByteCount(key!) < SecretKeyRequirements.MinimumKeyBytes)
        {
            return ValidateOptionsResult.Fail(
                $"Security violation: Gateway:RestApi:BulkStream:SigningKey must be at least {SecretKeyRequirements.MinimumKeyBytes} bytes long outside Development.");
        }

        return ValidateOptionsResult.Success;
    }
}
